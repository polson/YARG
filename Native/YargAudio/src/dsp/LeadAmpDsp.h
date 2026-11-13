#pragma once

#include "BassCoreBindings.h"
#include "yarg_audio.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace yarg::audio {

struct Cabinet {
    const float* samples = nullptr;
    std::size_t frames = 0;
    float rate = 0.0f;
};

class LeadAmp final {
public:
    static constexpr int MAX_CHANNELS = 8;
    static constexpr float MAX_GAIN = 16.0f;
    static constexpr int MAX_CABINET_FRAMES = 1024;
    static constexpr int TONE_BANDS = YARG_LEAD_AMP_TONE_BANDS;
    static constexpr float MIN_DRIVE = 0.125f;
    static constexpr float MAX_DRIVE = 8.0f;
    static constexpr float MAX_TONE_DB = 12.0f;
    static constexpr float TONE_LOWEST_HZ = 62.5f;
    static constexpr int TONE_BANDS_PER_OCTAVE = 3;
    static constexpr float WAH_MIN_HZ = 300.0f;
    static constexpr float WAH_MAX_HZ = 3000.0f;
    static constexpr float MIN_ECHO_SECONDS = 0.05f;
    static constexpr float MAX_ECHO_SECONDS = 0.9f;
    static constexpr int MAX_CABINETS = 8;

    explicit LeadAmp(int channels, float sampleRate, const Cabinet* cabinets = nullptr, std::size_t cabinetCount = 0);
    LeadAmp(int channels, float sampleRate, const float* cabinet, std::size_t cabinetFrames, float cabinetRate);

    void process(float* samples, std::size_t frames, float gain) noexcept;
    void setTone(const yarg_lead_amp_tone& tone) noexcept;
    std::size_t cabinetCount() const noexcept;

    struct Biquad {
        float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
    };

    static yarg_lead_amp_tone defaultTone() noexcept;
    static bool validTone(const yarg_lead_amp_tone& tone) noexcept;
    static float toneHz(int band) noexcept;
    static Biquad toneBand(float sampleRate, int band, float gainDb) noexcept;
    static Biquad peak(float sampleRate, float hz, float gainDb, float q) noexcept;

private:
    enum Filter {
        PRE_HIGH_PASS,
        PRE_BOOST,
        PRE_LOW_PASS,
        LOW_SHELF,
        MID_CUT,
        PRESENCE,
        CAB_HIGH_PASS,
        CAB_THUMP,
        CAB_BODY,
        CAB_BITE,
        CAB_HONK,
        CAB_LOW_PASS_1,
        CAB_LOW_PASS_2,
        TONE_FIRST,
        FILTER_COUNT = TONE_FIRST + TONE_BANDS,
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
        Shaper first, second;
        float couplingInput = 0, couplingOutput = 0;
        float interstage = 0;
        float echoTone = 0;
        std::vector<float> echo;
        int echoFrames = 1;
        std::vector<float> cabinetHistory;
        float wahBand = 0, wahLow = 0;
    };

    float run(int filter, ChannelState& state, float input) const noexcept;
    float convolve(ChannelState& state, float input) const noexcept;
    float noise(const float* frame) noexcept;
    void sweepWah(const float* frame) noexcept;
    float wah(ChannelState& state, float input) const noexcept;
    static std::vector<float> loadCabinet(const Cabinet& cabinet, float sampleRate);
    static float shape(Shaper& shaper, float input, float bias, float offset) noexcept;

    int channels_;
    float sampleRate_;
    std::array<Biquad, FILTER_COUNT> filters_{};
    float couplingCoefficient_;
    float interstageCoefficient_;
    float echoToneCoefficient_;
    float gainCoefficient_;
    float firstOffset_;
    float secondOffset_;
    float gain_ = -1.0f;
    float drive_ = -1.0f;
    float targetDrive_ = 1.0f;
    int echoIndex_ = 0;
    int echoLength_ = 1;
    float echoLevel_;
    std::vector<std::vector<float>> cabinets_;
    std::size_t cabinetLength_ = 0;
    std::size_t cabinet_ = 0;
    int cabinetIndex_ = 0;
    std::vector<float> hum_;
    std::size_t humIndex_ = 0;
    std::uint32_t hissState_ = 0x9E3779B9u;
    float noiseEnvelope_ = 0.0f;
    float noiseReleaseCoefficient_;
    bool wahOn_ = false;
    float wahStartOctaves_ = 0.0f;
    float wahEndOctaves_ = 0.0f;
    float wahFast_ = 0.0f;
    float wahSlow_ = 0.0f;
    float wahFastRelease_;
    float wahSlowCoefficient_;
    float wahSweepCoefficient_;
    float wahSweep_ = 0.0f;
    std::size_t wahSincePick_ = 0;
    std::size_t wahRefractoryFrames_;
    int wahControlFrame_ = 0;
    float wahA1_ = 1.0f, wahA2_ = 0.0f, wahA3_ = 0.0f;
    std::array<ChannelState, MAX_CHANNELS> states_{};
};

}

static_assert(sizeof(yarg_lead_amp_tone) % sizeof(std::uint32_t) == 0);

struct yarg_lead_amp_dsp {
    static constexpr std::size_t TONE_WORDS = sizeof(yarg_lead_amp_tone) / sizeof(std::uint32_t);

    yarg_lead_amp_dsp(const yarg::audio::BassCoreBindings& bindings, std::uint32_t channelHandle,
        int channels, float sampleRate, float gain, const yarg::audio::Cabinet* cabinets, std::size_t cabinetCount);

    const yarg::audio::BassCoreBindings& bass;
    std::uint32_t channel;
    std::uint32_t dsp = 0;
    int channelCount;
    std::atomic<std::uint32_t> gainBits;
    std::array<std::atomic<std::uint32_t>, TONE_WORDS> toneWords{};
    std::atomic<std::uint32_t> toneVersion{0};
    std::uint32_t appliedToneVersion = 0;
    yarg::audio::LeadAmp amp;
};

namespace yarg::audio {

void YARG_BASS_CALLBACK leadAmpDspProc(std::uint32_t dsp, std::uint32_t channel,
    void* buffer, std::uint32_t length, void* user) noexcept;

bool cabinetList(const float* samples, const std::uint32_t* frames, const float* rates, std::size_t count,
    std::vector<Cabinet>& cabinets);

int leadAmpDspAttach(const BassCoreBindings& bass, std::uint32_t channel, float gain,
    const Cabinet* cabinets, std::size_t cabinetCount,
    int priority, yarg_lead_amp_dsp** dsp, int* bassError) noexcept;
int leadAmpDspSetGain(yarg_lead_amp_dsp* dsp, float gain) noexcept;
int leadAmpDspSetTone(yarg_lead_amp_dsp* dsp, const yarg_lead_amp_tone* tone) noexcept;

int leadAmpMatch(float sampleRate, const Cabinet* cabinets, std::size_t cabinetCount,
    const float* stem, const float* di, const std::uint32_t* noteFrames, std::size_t noteCount,
    yarg_lead_amp_tone* tone) noexcept;

bool leadAmpDspDestroy(yarg_lead_amp_dsp* dsp) noexcept;

}
