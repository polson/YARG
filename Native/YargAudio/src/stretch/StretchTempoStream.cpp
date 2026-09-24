#include "stretch/StretchTempoStream.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace yarg::audio {
namespace {

constexpr std::uint32_t BASS_SAMPLE_FLOAT = 0x100;
constexpr std::uint32_t BASS_STREAM_DECODE = 0x200000;
constexpr std::uint32_t BASS_STREAMPROC_END = 0x80000000;
constexpr int BASS_ERROR_ENDED = 45;
constexpr int HISTORY_SECONDS = 12;
constexpr float STRIDE_MIN_SPEED = 0.1f;
constexpr float STRIDE_MAX_SPEED = 4.0f;
constexpr double STRIDE_MAX_DEBT_GAIN = 2.0;
constexpr double STRIDE_REPAY_GAIN = 0.5;
constexpr double WINDOW_SECONDS = 0.060;
constexpr int INTERVAL_DIVISOR = 8;
constexpr float TRANSIENT_MIN_HZ = 1500.0f;
constexpr double STRIDE_MAX_DEBT_SECONDS = 0.040;
constexpr double STRIDE_REPAY_FRACTION = 0.50;
constexpr float ATTACK_POWER_GATE = 2.25f;
constexpr float ATTACK_SMOOTH_RATIO = 0.5f;
constexpr float ATTACK_SLOPE = 0.12f;
constexpr float ATTACK_MEDIUM_SLOPE = 0.0f;

}

std::unique_ptr<StretchTempoStream> StretchTempoStream::create(
    BassCoreBindings& bass, std::uint32_t source, int* bassError,
    const StretchConfig& config) noexcept {
    *bassError = 0;
    BassChannelInfo info{};
    if (!bass.oneShotValid() || !bass.getChannelInfo(source, info)) {
        *bassError = bass.error();
        return nullptr;
    }
    if (info.frequency < 8000 || info.frequency > 192000 ||
        info.channels == 0 || info.channels > 8 ||
        (info.flags & (BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE)) !=
            (BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE)) {
        return nullptr;
    }
    try {
        auto result = std::unique_ptr<StretchTempoStream>(
            new StretchTempoStream(bass, source, info, config));
        result->stream_ = bass.createStream(info.frequency, info.channels,
            BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE, &callback, result.get());
        if (result->stream_ == 0) {
            *bassError = bass.error();
            return nullptr;
        }
        return result;
    } catch (...) {
        return nullptr;
    }
}

StretchTempoStream::StretchTempoStream(BassCoreBindings& bass,
    std::uint32_t source, const BassChannelInfo& info, const StretchConfig& config)
    : bass_(bass), source_(source), sampleRate_(info.frequency),
      channels_(info.channels) {
    const int windowSteps = static_cast<int>(std::ceil(sampleRate_ * WINDOW_SECONDS /
        (2 * BLOCK_FRAMES)));
    const int window = std::max(4 * BLOCK_FRAMES, windowSteps * 2 * BLOCK_FRAMES);
    stretch_.configure(channels_, sampleRate_, window, window / INTERVAL_DIVISOR);
    stretch_.setDistortionPolishEnabled(false);
    stretch_.setAdaptiveSidelobeLockEnabled(config.adaptiveSidelobeLock);
    stretch_.setAttackGainParams(ATTACK_POWER_GATE, ATTACK_SMOOTH_RATIO,
        ATTACK_SLOPE, ATTACK_MEDIUM_SLOPE);
    const float transientCutoff = std::clamp(TRANSIENT_MIN_HZ / sampleRate_, 0.001f, 0.49f);
    stretch_.setTransientFrequency(transientCutoff);
    const int capacity = std::max(stretch_.inputLatency(),
        static_cast<int>(MAX_SPEED * BLOCK_FRAMES) + 1);
    input_.resize(capacity * channels_);
    interleaved_.resize(input_.size());
    output_.resize(BLOCK_FRAMES * channels_);
    for (std::uint32_t channel = 0; channel < channels_; ++channel) {
        inputChannels_.push_back(input_.data() + channel * capacity);
        outputChannels_.push_back(output_.data() + channel * BLOCK_FRAMES);
    }
    history_.resize((sampleRate_ * HISTORY_SECONDS + stretch_.outputLatency()) /
        BLOCK_FRAMES + 2);
}

StretchTempoStream::~StretchTempoStream() {
    destroy();
}

bool StretchTempoStream::destroy() noexcept {
    if (stream_ == 0) {
        return true;
    }
    if (!bass_.freeStream(stream_)) {
        return false;
    }
    stream_ = 0;
    return true;
}

void StretchTempoStream::setSpeed(float speed, float pitch) noexcept {
    speed_.store(speed, std::memory_order_relaxed);
    pitch_.store(pitch, std::memory_order_relaxed);
}

void StretchTempoStream::reset() noexcept {
    stretch_.reset(0);
    std::lock_guard lock(historyMutex_);
    inputRemainder_.store(0, std::memory_order_relaxed);
    strideDebt_.store(0, std::memory_order_relaxed);
    processedBlocks_.store(0, std::memory_order_relaxed);
    inputFrames_.store(0, std::memory_order_relaxed);
    idealInputFrames_.store(0, std::memory_order_relaxed);
    sourceFrames_.store(0, std::memory_order_relaxed);
    deliveredFrames_.store(0, std::memory_order_relaxed);
    outputOffset_.store(BLOCK_FRAMES, std::memory_order_relaxed);
    primed_.store(false, std::memory_order_relaxed);
    ended_.store(false, std::memory_order_relaxed);
    protectedTransients_.store(0, std::memory_order_relaxed);
    std::fill(history_.begin(), history_.end(), PositionBlock{});
}

bool StretchTempoStream::flush() noexcept {
    if (!bass_.lockChannel(stream_, true)) {
        return false;
    }
    reset();
    return bass_.lockChannel(stream_, false);
}

bool StretchTempoStream::position(double outputFrame, double& seconds) const noexcept {
    return lookup(outputFrame, seconds, false);
}

bool StretchTempoStream::getPosition(std::int64_t bytes, double& seconds) noexcept {
    return lookup(static_cast<double>(bytes) /
        (channels_ * sizeof(float)), seconds, false);
}

bool StretchTempoStream::idealPosition(double outputFrame, double& seconds) const noexcept {
    return lookup(outputFrame, seconds, true);
}

bool StretchTempoStream::getIdealPosition(std::int64_t bytes, double& seconds) noexcept {
    return lookup(static_cast<double>(bytes) /
        (channels_ * sizeof(float)), seconds, true);
}

bool StretchTempoStream::lookup(double outputFrame, double& seconds, bool useIdeal) const noexcept {
    // Lock-free: scalars are atomic and history reads take historyMutex_, so a
    // game-thread query never waits on realtime audio processing (the old
    // BASS_ChannelLock here serialized queries against the STREAMPROC callback,
    // injecting millisecond-scale sampling jitter into sync measurements).
    const auto processed = processedBlocks_.load(std::memory_order_acquire);
    if (outputFrame == 0 && processed == 0) {
        seconds = 0;
        return true;
    }
    if (outputFrame < 0 || outputFrame >= processed * BLOCK_FRAMES) {
        return false;
    }
    const auto index = static_cast<std::uint64_t>(outputFrame / BLOCK_FRAMES);
    std::lock_guard lock(historyMutex_);
    const auto& block = history_[index % history_.size()];
    if (block.index != index) {
        return false;
    }
    const double fraction = (outputFrame - index * BLOCK_FRAMES) / BLOCK_FRAMES;
    const double firstFrame = useIdeal ? block.idealInputFrame : block.inputFrame;
    const int frameCount = useIdeal ? block.idealInputFrames : block.inputFrames;
    seconds = (firstFrame + fraction * frameCount) / sampleRate_;
    return true;
}

bool StretchTempoStream::readInput(int frames) noexcept {
    int readFrames = 0;
    while (readFrames < frames && !ended_.load(std::memory_order_relaxed)) {
        const int bytes = bass_.getData(source_,
            interleaved_.data() + readFrames * channels_,
            (frames - readFrames) * channels_ * sizeof(float));
        if (bytes < 0) {
            if (bass_.error() != BASS_ERROR_ENDED) {
                return false;
            }
            ended_.store(true, std::memory_order_relaxed);
        } else if (bytes == 0) {
            break;
        } else {
            readFrames += bytes / (channels_ * sizeof(float));
        }
    }
    sourceFrames_.fetch_add(static_cast<std::uint64_t>(readFrames), std::memory_order_relaxed);
    for (std::uint32_t channel = 0; channel < channels_; ++channel) {
        auto* input = inputChannels_[channel];
        for (int frame = 0; frame < readFrames; ++frame) {
            input[frame] = interleaved_[frame * channels_ + channel];
        }
        std::fill(input + readFrames, input + frames, 0.0f);
    }
    return true;
}

bool StretchTempoStream::process() noexcept {
    const auto speed = speed_.load(std::memory_order_relaxed);
    const auto pitch = pitch_.load(std::memory_order_relaxed);
    int steady = 0;
    const int frames = nextInputFrames(speed, steady);
    if (!readInput(frames)) {
        return false;
    }
    const auto blockIndex = processedBlocks_.load(std::memory_order_relaxed);
    const auto baseInput = inputFrames_.load(std::memory_order_relaxed);
    const auto idealBaseInput = idealInputFrames_.load(std::memory_order_relaxed);
    {
        std::lock_guard lock(historyMutex_);
        history_[blockIndex % history_.size()] = PositionBlock{blockIndex,
            static_cast<double>(baseInput), frames,
            static_cast<double>(idealBaseInput), steady};
    }
    stretch_.setTransposeFactor(pitch);
    stretch_.process(inputChannels_, frames, outputChannels_, BLOCK_FRAMES);
    protectedTransients_.store(stretch_.transientCount(), std::memory_order_relaxed);
    inputFrames_.fetch_add(static_cast<std::uint64_t>(frames), std::memory_order_relaxed);
    idealInputFrames_.fetch_add(static_cast<std::uint64_t>(steady), std::memory_order_relaxed);
    processedBlocks_.fetch_add(1, std::memory_order_release);
    return true;
}

int StretchTempoStream::nextInputFrames(float speed, int& steadyOut) noexcept {
    const double exact = speed * static_cast<double>(BLOCK_FRAMES) +
        inputRemainder_.load(std::memory_order_relaxed);
    const int steady = static_cast<int>(exact);
    steadyOut = steady;
    inputRemainder_.store(exact - steady, std::memory_order_relaxed);
    const bool live = !ended_.load(std::memory_order_relaxed);
    const double debt = strideDebt_.load(std::memory_order_relaxed);
    int frames = steady;
    // A drum hit only stays snappy if the stretcher sees it whole, so borrow
    // whole blocks while the hit lasts. Borrowed frames are paid back a little
    // per block afterwards, keeping long-term speed exact.
    if (live && speed >= STRIDE_MIN_SPEED && speed <= STRIDE_MAX_SPEED &&
        stretch_.inTransient() && stretch_.strideArmed()) {
        const double maxDebt = STRIDE_MAX_DEBT_SECONDS * sampleRate_ * STRIDE_MAX_DEBT_GAIN;
        const double newDebt = debt + (BLOCK_FRAMES - steady);
        double extra = BLOCK_FRAMES - steady;
        if (newDebt > maxDebt) {
            extra = maxDebt - debt;
        } else if (newDebt < -maxDebt) {
            extra = -maxDebt - debt;
        }
        frames = steady + static_cast<int>(extra);
    } else if (live && debt != 0.0) {
        const double step = std::max(1.0,
            STRIDE_REPAY_FRACTION * speed * BLOCK_FRAMES * STRIDE_REPAY_GAIN);
        const double repay = std::max(-step, std::min(debt, step));
        frames = steady - static_cast<int>(repay);
    }
    const int capacity = static_cast<int>(input_.size() / channels_);
    frames = std::max(0, std::min(frames, capacity));
    strideDebt_.store(debt + (frames - steady), std::memory_order_relaxed);
    return frames;
}

bool StretchTempoStream::prime() noexcept {
    // The stretcher needs old audio to make new audio, so feed it its required
    // head start, then throw away the warm-up output before anyone listens.
    const auto speed = speed_.load(std::memory_order_relaxed);
    const auto inputLatency = stretch_.inputLatency();
    const auto outputLatency = stretch_.outputLatency();
    if (!readInput(inputLatency)) {
        return false;
    }
    stretch_.seek(inputChannels_, inputLatency, speed);
    for (int discarded = 0; discarded < outputLatency;
         discarded += BLOCK_FRAMES) {
        if (!process()) {
            return false;
        }
    }
    return true;
}

std::uint32_t YARG_BASS_CALLBACK StretchTempoStream::callback(
    std::uint32_t, void* buffer, std::uint32_t length, void* user) noexcept {
    auto& stream = *static_cast<StretchTempoStream*>(user);
    const auto frameBytes = stream.channels_ * sizeof(float);
    if (length % frameBytes != 0) {
        std::memset(buffer, 0, length);
        return length;
    }
    return stream.read(static_cast<float*>(buffer), length / frameBytes);
}

std::uint32_t StretchTempoStream::read(float* output, std::uint32_t frames) noexcept {
    const auto frameBytes = channels_ * sizeof(float);
    if (!primed_.load(std::memory_order_relaxed)) {
        if (!prime()) {
            return BASS_STREAMPROC_END;
        }
        primed_.store(true, std::memory_order_relaxed);
    }
    std::uint32_t written = 0;
    auto delivered = deliveredFrames_.load(std::memory_order_relaxed);
    auto offset = outputOffset_.load(std::memory_order_relaxed);
    while (written < frames) {
        if (offset == BLOCK_FRAMES) {
            if (!process()) {
                break;
            }
            offset = 0;
        }
        if (ended_.load(std::memory_order_relaxed)) {
            // The song ends when everything read has been heard, not when reading stops.
            double positionSeconds = 0;
            if (position(static_cast<double>(delivered), positionSeconds) &&
                positionSeconds * sampleRate_ >=
                    sourceFrames_.load(std::memory_order_relaxed)) {
                break;
            }
        }
        for (std::uint32_t channel = 0; channel < channels_; ++channel) {
            output[written * channels_ + channel] = outputChannels_[channel][offset];
        }
        ++offset;
        ++delivered;
        ++written;
    }
    outputOffset_.store(offset, std::memory_order_relaxed);
    deliveredFrames_.store(delivered, std::memory_order_relaxed);
    if (written < frames) {
        return static_cast<std::uint32_t>(written * frameBytes) | BASS_STREAMPROC_END;
    }
    return written * frameBytes;
}

}
