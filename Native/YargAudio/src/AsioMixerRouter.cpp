#include "AsioMixerRouter.h"
#include "AudioMath.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <limits>
#include <thread>

namespace yarg::audio {

class AsioMixerRouter::BassAudioSource final : public IAudioSource {
public:
    BassAudioSource(BassBindings& bass, int device, std::uint32_t handle,
        std::uint32_t channels)
        : bass_(bass), device_(device), handle_(handle), channels_(channels) {}

    bool prepareThread() noexcept override { return bass_.setDevice(device_); }

    int read(float* samples, std::size_t frames) noexcept override {
        const auto bytes = frames * channels_ * sizeof(float);
        const int result = bass_.getData(handle_, samples, static_cast<std::uint32_t>(bytes));
        return result < 0 ? -1 : result / static_cast<int>(channels_ * sizeof(float));
    }

    int lastError() const noexcept override { return bass_.bassError(); }
    std::int64_t position(std::uint32_t sourceHandle,
        std::uint32_t delayBytes) noexcept override {
        return bass_.mixerGetPosition(sourceHandle, delayBytes);
    }

private:
    BassBindings& bass_;
    int device_;
    std::uint32_t handle_;
    std::uint32_t channels_;
};

AsioMixerRouter::AsioMixerRouter(const yarg_asio_router_config& config)
    : config_(config), directScratch_(
        static_cast<std::size_t>(config.callback_frames) * config.channels) {
    LARGE_INTEGER frequency{};
    if (QueryPerformanceFrequency(&frequency)) performanceFrequency_ = frequency.QuadPart;
}

AsioMixerRouter::~AsioMixerRouter() {
    state_.store(YARG_ASIO_ROUTER_STOPPING, std::memory_order_release);
    disableOutput();
    if (buffered_) buffered_->stop();
    state_.store(YARG_ASIO_ROUTER_STOPPED, std::memory_order_release);
}

int AsioMixerRouter::initialize() noexcept {
    if (!bass_.load()) {
        lastError_.store(YARG_AUDIO_ERROR_DEPENDENCY);
        return YARG_AUDIO_ERROR_DEPENDENCY;
    }
    return YARG_AUDIO_OK;
}

int AsioMixerRouter::attach(std::uint32_t mixer,
    std::uint32_t bufferMilliseconds) noexcept {
    if (mixer == 0 || outputEnabled_.load()) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;

    if (bufferMilliseconds == 0) {
        if (directHandle_ != 0) return YARG_AUDIO_ERROR_UNSUPPORTED;
        directHandle_ = mixer;
    } else {
        if (buffered_) return YARG_AUDIO_ERROR_UNSUPPORTED;
        try {
            auto source = std::make_unique<BassAudioSource>(
                bass_, config_.bass_device_id, mixer, config_.channels);
            buffered_ = std::make_unique<RenderAheadMixer>(std::move(source),
                config_.sample_rate, config_.channels, config_.callback_frames,
                bufferMilliseconds);
            bufferedHandle_ = mixer;
            if (!buffered_->start()) {
                buffered_.reset();
                bufferedHandle_ = 0;
                return YARG_AUDIO_ERROR_INTERNAL;
            }
            minimumQueuedFrames_.store(
                static_cast<std::uint32_t>(buffered_->targetFrames()));
        } catch (...) {
            return YARG_AUDIO_ERROR_INTERNAL;
        }
    }

    state_.store(YARG_ASIO_ROUTER_ATTACHED, std::memory_order_release);
    return YARG_AUDIO_OK;
}

int AsioMixerRouter::prefill(std::uint32_t mixer,
    std::uint32_t timeoutMilliseconds) noexcept {
    // Refill while ASIO runs after flush. Song gate stays closed until target is ready;
    // direct live audio continues through callback.
    if (!buffered_ || mixer != bufferedHandle_)
        return YARG_AUDIO_ERROR_INVALID_STATE;

    state_.store(YARG_ASIO_ROUTER_PREFILLING, std::memory_order_release);
    if (!buffered_->prefill(std::chrono::milliseconds(timeoutMilliseconds))) {
        if (buffered_->failed()) {
            lastError_.store(buffered_->lastError());
            state_.store(YARG_ASIO_ROUTER_SOURCE_FAILED, std::memory_order_release);
            return YARG_AUDIO_ERROR_SOURCE;
        }
        state_.store(YARG_ASIO_ROUTER_ATTACHED, std::memory_order_release);
        return YARG_AUDIO_ERROR_TIMEOUT;
    }
    state_.store(YARG_ASIO_ROUTER_READY, std::memory_order_release);
    songEnabled_.store(true, std::memory_order_release);
    return YARG_AUDIO_OK;
}

int AsioMixerRouter::enableOutput(std::uint32_t firstChannel) noexcept {
    if (!buffered_ || directHandle_ == 0 || outputEnabled_.load())
        return YARG_AUDIO_ERROR_INVALID_STATE;

    firstOutputChannel_ = firstChannel;
    if (!bass_.asioSetFloat(firstChannel) ||
        !bass_.asioSetRate(firstChannel, config_.sample_rate) ||
        !bass_.asioEnable(firstChannel, &AsioMixerRouter::outputCallback, this)) {
        lastError_.store(bass_.asioError());
        return YARG_AUDIO_ERROR_BASS_ASIO;
    }

    outputEnabled_.store(true, std::memory_order_release);
    if (!bass_.asioJoin(firstChannel + 1, firstChannel)) {
        lastError_.store(bass_.asioError());
        disableOutput();
        return YARG_AUDIO_ERROR_BASS_ASIO;
    }

    state_.store(YARG_ASIO_ROUTER_RUNNING, std::memory_order_release);
    return YARG_AUDIO_OK;
}

int AsioMixerRouter::flush(std::uint32_t mixer) noexcept {
    if (!buffered_ || mixer != bufferedHandle_) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    songEnabled_.store(false, std::memory_order_release);
    while (activeSongConsumers_.load(std::memory_order_acquire) != 0) {
        std::this_thread::yield();
    }
    if (!buffered_->clear()) return YARG_AUDIO_ERROR_INTERNAL;
    state_.store(YARG_ASIO_ROUTER_ATTACHED, std::memory_order_release);
    return YARG_AUDIO_OK;
}

int64_t AsioMixerRouter::getSourcePosition(std::uint32_t source,
    std::uint32_t outputLatencyFrames, int& error) noexcept {
    if (!buffered_ || source == 0) {
        error = YARG_AUDIO_ERROR_INVALID_ARGUMENT;
        return -1;
    }

    std::uint64_t elapsedFrames = 0;
    const auto callbackTimestamp = callbackTimestamp_.load(std::memory_order_acquire);
    if (callbackTimestamp > 0 && performanceFrequency_ > 0) {
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        const auto elapsed = std::max<std::int64_t>(0, now.QuadPart - callbackTimestamp);
        elapsedFrames = static_cast<std::uint64_t>(elapsed) * config_.sample_rate /
            static_cast<std::uint64_t>(performanceFrequency_);
    }

    const auto queued = static_cast<std::uint64_t>(buffered_->queuedFrames());
    const auto hardwareDelay = outputLatencyFrames > elapsedFrames
        ? outputLatencyFrames - elapsedFrames : 0;
    const auto delayFrames = std::min<std::uint64_t>(
        queued + hardwareDelay, UINT32_MAX / (config_.channels * sizeof(float)));
    const auto delayBytes = static_cast<std::uint32_t>(
        delayFrames * config_.channels * sizeof(float));
    auto position = buffered_->sourcePosition(source, delayBytes);
    if (position < 0 && bass_.bassError() == 37) {
        position = buffered_->sourcePosition(source, 0);
    }
    error = position < 0 ? YARG_AUDIO_ERROR_BASS : YARG_AUDIO_OK;
    return position;
}

int AsioMixerRouter::getStats(yarg_asio_router_stats& stats) const noexcept {
    stats.state = state_.load(std::memory_order_acquire);
    stats.last_error = lastError_.load(std::memory_order_relaxed);
    stats.queued_frames = buffered_
        ? static_cast<std::uint32_t>(buffered_->queuedFrames()) : 0;
    const auto minimum = minimumQueuedFrames_.load(std::memory_order_relaxed);
    stats.minimum_queued_frames = minimum == UINT32_MAX ? stats.queued_frames : minimum;
    stats.produced_frames = buffered_ ? buffered_->producedFrames() : 0;
    stats.consumed_song_frames = consumedSongFrames_.load(std::memory_order_relaxed);
    stats.requested_output_frames = requestedOutputFrames_.load(std::memory_order_relaxed);
    stats.underrun_frames = underrunFrames_.load(std::memory_order_relaxed);
    stats.underrun_events = underrunEvents_.load(std::memory_order_relaxed);
    stats.maximum_render_nanoseconds = buffered_ ? buffered_->maximumRenderNanoseconds() : 0;
    return YARG_AUDIO_OK;
}

int AsioMixerRouter::setVolume(float volume) noexcept {
    if (!std::isfinite(volume) || volume < 0.0f) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    volume_.store(volume, std::memory_order_release);
    return YARG_AUDIO_OK;
}

std::uint32_t CALLBACK AsioMixerRouter::outputCallback(int input, std::uint32_t,
    void* buffer, std::uint32_t length, void* user) noexcept {
    if (input || !user || !buffer) return 0;
    return static_cast<AsioMixerRouter*>(user)->processOutput(buffer, length);
}

std::uint32_t AsioMixerRouter::processOutput(void* buffer, std::uint32_t length) noexcept {
    activeCallbacks_.fetch_add(1, std::memory_order_acq_rel);
    struct CallbackExit {
        std::atomic<std::uint32_t>& count;
        ~CallbackExit() { count.fetch_sub(1, std::memory_order_acq_rel); }
    } callbackExit{activeCallbacks_};

    auto* output = static_cast<float*>(buffer);
    std::memset(output, 0, length);
    const auto bytesPerFrame = config_.channels * sizeof(float);
    if (state_.load(std::memory_order_acquire) == YARG_ASIO_ROUTER_STOPPING ||
        bytesPerFrame == 0 || length % bytesPerFrame != 0) return length;

    const auto frames = length / bytesPerFrame;
    LARGE_INTEGER timestamp{};
    if (QueryPerformanceCounter(&timestamp)) {
        callbackTimestamp_.store(timestamp.QuadPart, std::memory_order_release);
    }
    requestedOutputFrames_.fetch_add(frames, std::memory_order_relaxed);
    std::size_t consumed = 0;
    const bool songRequested = songEnabled_.load(std::memory_order_acquire);
    if (songRequested) {
        activeSongConsumers_.fetch_add(1, std::memory_order_acq_rel);
        if (songEnabled_.load(std::memory_order_acquire)) {
            consumed = buffered_->consume(output, frames);
        }
        activeSongConsumers_.fetch_sub(1, std::memory_order_acq_rel);
    }
    consumedSongFrames_.fetch_add(consumed, std::memory_order_relaxed);
    const auto queued = static_cast<std::uint32_t>(buffered_->queuedFrames());
    updateMinimum(queued);

    if (buffered_->failed()) {
        lastError_.store(buffered_->lastError(), std::memory_order_relaxed);
        state_.store(YARG_ASIO_ROUTER_SOURCE_FAILED, std::memory_order_release);
    } else if (songRequested && consumed < frames) {
        underrunFrames_.fetch_add(frames - consumed, std::memory_order_relaxed);
        underrunEvents_.fetch_add(1, std::memory_order_relaxed);
        state_.store(YARG_ASIO_ROUTER_STARVED, std::memory_order_release);
    } else if (songRequested) {
        state_.store(YARG_ASIO_ROUTER_RUNNING, std::memory_order_release);
    }

    if (frames <= config_.callback_frames && bass_.setDevice(config_.bass_device_id)) {
        const int directBytes = bass_.getData(directHandle_, directScratch_.data(), length);
        if (directBytes < 0) {
            lastError_.store(bass_.bassError(), std::memory_order_relaxed);
        } else {
            const auto directSamples = static_cast<std::size_t>(directBytes) / sizeof(float);
            mixAdd(output, directScratch_.data(), directSamples);
        }
    } else if (frames > config_.callback_frames) {
        lastError_.store(YARG_AUDIO_ERROR_UNSUPPORTED, std::memory_order_relaxed);
    } else {
        lastError_.store(bass_.bassError(), std::memory_order_relaxed);
    }

    const float volume = volume_.load(std::memory_order_acquire);
    const auto sampleCount = static_cast<std::size_t>(frames) * config_.channels;
    applyGain(output, sampleCount, volume);
    return length;
}

void AsioMixerRouter::disableOutput() noexcept {
    if (!outputEnabled_.exchange(false, std::memory_order_acq_rel)) return;
    bass_.asioResetEnable(firstOutputChannel_);
    while (activeCallbacks_.load(std::memory_order_acquire) != 0) {
        std::this_thread::yield();
    }
}

void AsioMixerRouter::updateMinimum(std::uint32_t queued) noexcept {
    auto previous = minimumQueuedFrames_.load(std::memory_order_relaxed);
    while (queued < previous && !minimumQueuedFrames_.compare_exchange_weak(
        previous, queued, std::memory_order_relaxed)) {
    }
}

} // namespace yarg::audio
