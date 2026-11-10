#pragma once

#include "stretch/YargStretch.h"

#include <cstdint>
#include <vector>

namespace yarg::audio {

// Offline time stretch at a constant speed over a caller-owned interleaved buffer.
// After begin(startFrame), output frame n maps to input frame startFrame + n * speed.
// Reads move forward only; skipped output is rendered and discarded.
class StretchRender final {
public:
    static constexpr int BLOCK_FRAMES = 256;
    static constexpr float MIN_SPEED = 0.05f;
    static constexpr float MAX_SPEED = 4.0f;
    static constexpr float MIN_PITCH = 0.25f;
    static constexpr float MAX_PITCH = 4.0f;

    StretchRender(int channels, int sampleRate, float speed);
    StretchRender(const StretchRender&) = delete;
    StretchRender& operator=(const StretchRender&) = delete;

    void setPitch(float factor, float baseHz);
    void begin(const float* input, std::int64_t inputFrames, std::int64_t startFrame);
    bool read(const float* input, std::int64_t inputFrames, std::int64_t outputFrame,
        float* output, int outputFrames);
    std::int64_t outputPosition() const noexcept { return outputPosition_; }

private:
    void loadInput(const float* input, std::int64_t inputFrames, std::int64_t firstFrame, int frames);
    void render(const float* input, std::int64_t inputFrames, int outputFrames, float* output);

    const int channels_;
    const int sampleRate_;
    const double speed_;
    YargStretch<float> stretch_{0};
    std::int64_t startFrame_ = 0;
    std::int64_t fedFrames_ = 0;
    std::int64_t renderedFrames_ = 0;
    std::int64_t outputPosition_ = 0;
    std::vector<float> inputBuffer_;
    std::vector<float> outputBuffer_;
    std::vector<float*> inputChannels_;
    std::vector<float*> outputChannels_;
    int capacity_ = 0;
};

}

struct yarg_stretch_render {
    yarg_stretch_render(int channels, int sampleRate, float speed) : value(channels, sampleRate, speed) {}
    yarg::audio::StretchRender value;
};
