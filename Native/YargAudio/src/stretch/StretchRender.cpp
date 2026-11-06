#include "stretch/StretchRender.h"

#include <algorithm>
#include <cmath>

namespace yarg::audio {
namespace {

constexpr double WINDOW_SECONDS = 0.060;
constexpr int INTERVAL_DIVISOR = 8;
constexpr float TRANSIENT_MIN_HZ = 1500.0f;

}

StretchRender::StretchRender(int channels, int sampleRate, float speed)
    : channels_(channels), speed_(speed) {
    const int windowSteps = static_cast<int>(std::ceil(sampleRate * WINDOW_SECONDS /
        (2 * BLOCK_FRAMES)));
    const int window = std::max(4 * BLOCK_FRAMES, windowSteps * 2 * BLOCK_FRAMES);
    stretch_.configure(channels, static_cast<float>(sampleRate), window, window / INTERVAL_DIVISOR);
    stretch_.setTransientFrequency(std::clamp(TRANSIENT_MIN_HZ / sampleRate, 0.001f, 0.49f));
    capacity_ = std::max(stretch_.seekLength(),
        static_cast<int>(std::ceil(MAX_SPEED * BLOCK_FRAMES)) + 2);
    inputBuffer_.resize(static_cast<std::size_t>(capacity_) * channels);
    outputBuffer_.resize(static_cast<std::size_t>(BLOCK_FRAMES) * channels);
    for (int channel = 0; channel < channels; ++channel) {
        inputChannels_.push_back(inputBuffer_.data() + channel * capacity_);
        outputChannels_.push_back(outputBuffer_.data() + channel * BLOCK_FRAMES);
    }
}

void StretchRender::begin(const float* input, std::int64_t inputFrames, std::int64_t startFrame) {
    // Seeking with inputLatency() frames past the start centres analysis on startFrame;
    // discarding outputLatency() frames then aligns output frame 0 with it.
    stretch_.reset(0);
    startFrame_ = startFrame;
    const int latency = stretch_.inputLatency();
    const int seekFrames = stretch_.seekLength();
    loadInput(input, inputFrames, startFrame + latency - seekFrames, seekFrames);
    stretch_.seek(inputChannels_, seekFrames, speed_);
    fedFrames_ = latency;
    renderedFrames_ = 0;
    render(input, inputFrames, stretch_.outputLatency(), nullptr);
    outputPosition_ = 0;
}

bool StretchRender::read(const float* input, std::int64_t inputFrames, std::int64_t outputFrame,
    float* output, int outputFrames) {
    if (outputFrame < outputPosition_) {
        return false;
    }
    while (outputPosition_ < outputFrame) {
        const auto skipped = std::min<std::int64_t>(outputFrame - outputPosition_, BLOCK_FRAMES);
        render(input, inputFrames, static_cast<int>(skipped), nullptr);
    }
    render(input, inputFrames, outputFrames, output);
    return true;
}

void StretchRender::loadInput(const float* input, std::int64_t inputFrames, std::int64_t firstFrame,
    int frames) {
    for (int channel = 0; channel < channels_; ++channel) {
        auto* destination = inputChannels_[channel];
        for (int frame = 0; frame < frames; ++frame) {
            const auto source = firstFrame + frame;
            destination[frame] = source >= 0 && source < inputFrames
                ? input[source * channels_ + channel] : 0.0f;
        }
    }
}

void StretchRender::render(const float* input, std::int64_t inputFrames, int outputFrames, float* output) {
    // Input is fed from the absolute rendered count so rounding never drifts.
    const int latency = stretch_.inputLatency();
    int written = 0;
    while (written < outputFrames) {
        const int frames = std::min(outputFrames - written, BLOCK_FRAMES);
        const auto target = latency + static_cast<std::int64_t>(
            std::floor(speed_ * static_cast<double>(renderedFrames_ + frames)));
        const int inputCount = static_cast<int>(target - fedFrames_);
        loadInput(input, inputFrames, startFrame_ + fedFrames_, inputCount);
        stretch_.process(inputChannels_, inputCount, outputChannels_, frames);
        fedFrames_ = target;
        renderedFrames_ += frames;
        outputPosition_ += frames;
        if (output) {
            for (int frame = 0; frame < frames; ++frame) {
                for (int channel = 0; channel < channels_; ++channel) {
                    output[(written + frame) * channels_ + channel] = outputChannels_[channel][frame];
                }
            }
        }
        written += frames;
    }
}

}
