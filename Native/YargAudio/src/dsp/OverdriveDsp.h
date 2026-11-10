#pragma once

#include "BassCoreBindings.h"
#include "yarg_audio.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace yarg::audio {

class Overdrive final {
public:
    static constexpr int MAX_CHANNELS = 8;
    static constexpr float MAX_DRIVE = 20.0f;

    Overdrive(int channels, float sampleRate) noexcept;

    void process(float* samples, std::size_t frames, float mix, float drive) noexcept;

private:
    struct Biquad {
        float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
    };

    struct Shaper {
        float lastInput = 0;
        float lastAntiderivative = 0;
    };

    struct ChannelState {
        float highPass1 = 0, highPass2 = 0;
        float mid1 = 0, mid2 = 0;
        float coupling1 = 0, coupling2 = 0;
        float lowPass1 = 0, lowPass2 = 0;
        Shaper first, second;
        float dcInput = 0, dcOutput = 0;
        float dryPower = 0, wetPower = 0;
        float makeup = 1;
        float dryLoudness = 0, outputLoudness = 0;
        float ceilingGain = 1;
        float peak = 0;
    };

    void reset() noexcept;
    static float filter(const Biquad& biquad, float& z1, float& z2, float input) noexcept;
    static float shape(Shaper& shaper, float input, float bias, float offset) noexcept;

    int channels_;
    Biquad highPass_;
    Biquad mid_;
    Biquad coupling_;
    Biquad lowPass_;
    float dcCoefficient_;
    float mixCoefficient_;
    float levelCoefficient_;
    float loudnessCoefficient_;
    float ceilingAttack_;
    float ceilingRelease_;
    float peakRelease_;
    float firstOffset_;
    float secondOffset_;
    float mix_ = 0;
    bool bypassed_ = true;
    std::array<ChannelState, MAX_CHANNELS> states_{};
};

}

struct yarg_overdrive_dsp {
    yarg_overdrive_dsp(const yarg::audio::BassCoreBindings& bindings, std::uint32_t channelHandle,
        int channels, float sampleRate, float mix, float drive) noexcept;

    const yarg::audio::BassCoreBindings& bass;
    std::uint32_t channel;
    std::uint32_t dsp = 0;
    int channelCount;
    std::atomic<std::uint32_t> mixBits;
    std::atomic<std::uint32_t> driveBits;
    yarg::audio::Overdrive overdrive;
};

namespace yarg::audio {

void YARG_BASS_CALLBACK overdriveDspProc(std::uint32_t dsp, std::uint32_t channel,
    void* buffer, std::uint32_t length, void* user) noexcept;

int overdriveDspAttach(const BassCoreBindings& bass, std::uint32_t channel, float mix, float drive,
    int priority, yarg_overdrive_dsp** dsp, int* bassError) noexcept;
int overdriveDspSet(yarg_overdrive_dsp* dsp, float mix, float drive) noexcept;

bool overdriveDspDestroy(yarg_overdrive_dsp* dsp) noexcept;

}
