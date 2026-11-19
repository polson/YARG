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
    static constexpr int MAX_DELAY_FRAMES = 8192;

    enum Filter {
        HIGH_PASS,
        MID_BOOST,
        COUPLING,
        SCOOP,
        TREBLE,
        PRESENCE,
        CAB_HIGH_PASS,
        CAB_BODY,
        CAB_BOX,
        CAB_PRESENCE,
        CAB_BITE,
        CAB_NOTCH,
        CAB_LOW_PASS_1,
        CAB_LOW_PASS_2,
        FILTER_COUNT,
    };

    struct Biquad {
        float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
    };

    struct BiquadState {
        float z1 = 0, z2 = 0;
    };

    struct Shaper {
        float lastInput = 0;
        float lastAntiderivative = 0;
    };

    struct ChannelState {
        std::array<BiquadState, FILTER_COUNT> filters{};
        Shaper first, second, power;
        float sag = 0;
        float roomTone = 0;
        float dcInput = 0, dcOutput = 0;
        float dryPower = 0, wetPower = 0;
        float makeup = 0;
        float dryLoudness = 0, outputLoudness = 0;
        float ceilingGain = 1;
        float peak = 0;
    };

    void reset() noexcept;
    float run(int filter, ChannelState& state, float input) const noexcept;
    float delayed(int channel, int frames) const noexcept;
    static float shape(Shaper& shaper, float input, float bias, float offset) noexcept;

    int channels_;
    std::array<Biquad, FILTER_COUNT> filters_{};
    std::array<int, 2> cabinetDelays_{};
    std::array<int, 5> roomDelays_{};
    int stereoOffset_;
    float roomToneCoefficient_;
    float sagAttack_;
    float sagRelease_;
    float dcCoefficient_;
    float mixCoefficient_;
    float levelCoefficient_;
    float loudnessCoefficient_;
    float ceilingAttack_;
    float ceilingRelease_;
    float peakRelease_;
    float firstOffset_;
    float secondOffset_;
    std::array<std::array<float, MAX_DELAY_FRAMES>, MAX_CHANNELS> history_{};
    int historyIndex_ = 0;
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
