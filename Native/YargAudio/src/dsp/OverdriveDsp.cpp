#include "dsp/OverdriveDsp.h"

#include "BitCastCompat.h"

#include <algorithm>
#include <cmath>
#include <new>

static_assert(std::atomic<std::uint32_t>::is_always_lock_free);

namespace {

constexpr std::uint32_t BassSampleFloat = 0x100;
constexpr std::uint32_t BassConfigFloatDsp = 9;

constexpr double PI = 3.14159265358979323846;
constexpr float FILTER_Q = 0.7071f;
constexpr float HIGH_PASS_HZ = 90.0f;
constexpr float MID_BOOST_HZ = 600.0f;
constexpr float MID_BOOST_DB = 5.0f;
constexpr float MID_BOOST_Q = 0.7f;
constexpr float COUPLING_HZ = 200.0f;
constexpr float SCOOP_HZ = 450.0f;
constexpr float SCOOP_DB = -3.0f;
constexpr float SCOOP_Q = 0.7f;
constexpr float TREBLE_HZ = 2500.0f;
constexpr float TREBLE_DB = 2.0f;
constexpr float TREBLE_Q = 0.8f;
constexpr float PRESENCE_HZ = 4000.0f;
constexpr float PRESENCE_DB = 2.0f;
constexpr float PRESENCE_Q = 0.8f;
constexpr float CAB_HIGH_PASS_HZ = 75.0f;
constexpr float CAB_BODY_HZ = 110.0f;
constexpr float CAB_BODY_DB = 4.0f;
constexpr float CAB_BODY_Q = 1.2f;
constexpr float CAB_BOX_HZ = 400.0f;
constexpr float CAB_BOX_DB = -2.5f;
constexpr float CAB_BOX_Q = 1.0f;
constexpr float CAB_PRESENCE_HZ = 2200.0f;
constexpr float CAB_PRESENCE_DB = 4.0f;
constexpr float CAB_PRESENCE_Q = 1.4f;
constexpr float CAB_BITE_HZ = 3500.0f;
constexpr float CAB_BITE_DB = 2.0f;
constexpr float CAB_BITE_Q = 2.0f;
constexpr float CAB_NOTCH_HZ = 4700.0f;
constexpr float CAB_NOTCH_DB = -6.0f;
constexpr float CAB_NOTCH_Q = 3.0f;
constexpr float CAB_LOW_PASS_HZ = 5500.0f;
constexpr float CAB_LOW_PASS_Q1 = 0.5412f;
constexpr float CAB_LOW_PASS_Q2 = 1.3066f;
constexpr float CABINET_DELAYS_MS[] = {0.8f, 1.9f};
constexpr float CABINET_GAINS[] = {0.22f, -0.14f};
constexpr float ROOM_DELAYS_MS[] = {7.1f, 11.3f, 16.9f, 23.7f, 31.3f};
constexpr float ROOM_GAINS[] = {0.30f, -0.24f, 0.20f, -0.15f, 0.11f};
constexpr float ROOM_STEREO_OFFSET_MS = 0.6f;
constexpr float ROOM_TONE_HZ = 3500.0f;
constexpr float ROOM_LEVEL = 0.35f;
constexpr float DC_BLOCK_HZ = 10.0f;
constexpr float FIRST_BIAS = 0.3f;
constexpr float SECOND_BIAS = -0.1f;
constexpr float SECOND_STAGE_GAIN = 10.0f;
constexpr float POWER_GAIN = 2.5f;
constexpr float SAG_AMOUNT = 1.5f;
constexpr float SAG_ATTACK_MS = 5.0f;
constexpr float SAG_RELEASE_MS = 120.0f;
constexpr float MIX_SMOOTHING_MS = 10.0f;
constexpr float LEVEL_SMOOTHING_MS = 20.0f;
constexpr float LOUDNESS_SMOOTHING_MS = 10.0f;
constexpr float CEILING_ATTACK_MS = 3.0f;
constexpr float SOFT_KNEE = 0.75f;
constexpr float CEILING_RELEASE_MS = 40.0f;
constexpr float PEAK_RELEASE_MS = 150.0f;
constexpr float MAX_MAKEUP = 2.0f;
constexpr float POWER_FLOOR = 1e-8f;
constexpr float BYPASS_MIX = 1e-5f;
constexpr float ADAA_MIN_STEP = 1e-4f;

int delayFrames(float milliseconds, float sampleRate, int maxFrames) noexcept {
    return std::clamp(static_cast<int>(std::lround(milliseconds * 0.001f * sampleRate)), 1, maxFrames - 1);
}

float smoothingCoefficient(float milliseconds, float sampleRate) noexcept {
    return static_cast<float>(1.0 - std::exp(-1.0 / (milliseconds * 0.001 * sampleRate)));
}

float softLimit(float value, float limit) noexcept {
    if (limit <= 0.0f) return 0.0f;
    const float magnitude = std::abs(value) / limit;
    if (magnitude <= SOFT_KNEE) return value;
    const float knee = 1.0f - SOFT_KNEE;
    const float limited = SOFT_KNEE + knee * std::tanh((magnitude - SOFT_KNEE) / knee);
    return std::copysign(limited * limit, value);
}

float logCosh(float value) noexcept {
    const float magnitude = std::abs(value);
    return magnitude + std::log1p(std::exp(-2.0f * magnitude)) - 0.69314718f;
}


}

namespace yarg::audio {

Overdrive::Overdrive(int channels, float sampleRate) noexcept
    : channels_(std::clamp(channels, 1, MAX_CHANNELS)),
      stereoOffset_(delayFrames(ROOM_STEREO_OFFSET_MS, sampleRate, MAX_DELAY_FRAMES)),
      roomToneCoefficient_(static_cast<float>(1.0 - std::exp(-2.0 * PI * ROOM_TONE_HZ / sampleRate))),
      sagAttack_(smoothingCoefficient(SAG_ATTACK_MS, sampleRate)),
      sagRelease_(smoothingCoefficient(SAG_RELEASE_MS, sampleRate)),
      dcCoefficient_(static_cast<float>(std::exp(-2.0 * PI * DC_BLOCK_HZ / sampleRate))),
      mixCoefficient_(smoothingCoefficient(MIX_SMOOTHING_MS, sampleRate)),
      levelCoefficient_(smoothingCoefficient(LEVEL_SMOOTHING_MS, sampleRate)),
      loudnessCoefficient_(smoothingCoefficient(LOUDNESS_SMOOTHING_MS, sampleRate)),
      ceilingAttack_(smoothingCoefficient(CEILING_ATTACK_MS, sampleRate)),
      ceilingRelease_(smoothingCoefficient(CEILING_RELEASE_MS, sampleRate)),
      peakRelease_(static_cast<float>(std::exp(-1.0 / (PEAK_RELEASE_MS * 0.001 * sampleRate)))),
      firstOffset_(std::tanh(FIRST_BIAS)),
      secondOffset_(std::tanh(SECOND_BIAS)) {
    const auto angle = [sampleRate](float hz) {
        return 2.0 * PI * std::min(hz, 0.45f * sampleRate) / sampleRate;
    };
    const auto pass = [&angle](float hz, bool highPass, float q) {
        const double w0 = angle(hz);
        const double cosine = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        const double a0 = 1.0 + alpha;
        const double edge = highPass ? (1.0 + cosine) / 2.0 : (1.0 - cosine) / 2.0;
        Biquad biquad;
        biquad.b0 = static_cast<float>(edge / a0);
        biquad.b1 = static_cast<float>((highPass ? -2.0 * edge : 2.0 * edge) / a0);
        biquad.b2 = static_cast<float>(edge / a0);
        biquad.a1 = static_cast<float>(-2.0 * cosine / a0);
        biquad.a2 = static_cast<float>((1.0 - alpha) / a0);
        return biquad;
    };
    const auto peak = [&angle](float hz, float gainDb, float q) {
        const double w0 = angle(hz);
        const double cosine = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        const double amplitude = std::pow(10.0, gainDb / 40.0);
        const double a0 = 1.0 + alpha / amplitude;
        Biquad biquad;
        biquad.b0 = static_cast<float>((1.0 + alpha * amplitude) / a0);
        biquad.b1 = static_cast<float>(-2.0 * cosine / a0);
        biquad.b2 = static_cast<float>((1.0 - alpha * amplitude) / a0);
        biquad.a1 = static_cast<float>(-2.0 * cosine / a0);
        biquad.a2 = static_cast<float>((1.0 - alpha / amplitude) / a0);
        return biquad;
    };
    filters_[HIGH_PASS] = pass(HIGH_PASS_HZ, true, FILTER_Q);
    filters_[MID_BOOST] = peak(MID_BOOST_HZ, MID_BOOST_DB, MID_BOOST_Q);
    filters_[COUPLING] = pass(COUPLING_HZ, true, FILTER_Q);
    filters_[SCOOP] = peak(SCOOP_HZ, SCOOP_DB, SCOOP_Q);
    filters_[TREBLE] = peak(TREBLE_HZ, TREBLE_DB, TREBLE_Q);
    filters_[PRESENCE] = peak(PRESENCE_HZ, PRESENCE_DB, PRESENCE_Q);
    filters_[CAB_HIGH_PASS] = pass(CAB_HIGH_PASS_HZ, true, FILTER_Q);
    filters_[CAB_BODY] = peak(CAB_BODY_HZ, CAB_BODY_DB, CAB_BODY_Q);
    filters_[CAB_BOX] = peak(CAB_BOX_HZ, CAB_BOX_DB, CAB_BOX_Q);
    filters_[CAB_PRESENCE] = peak(CAB_PRESENCE_HZ, CAB_PRESENCE_DB, CAB_PRESENCE_Q);
    filters_[CAB_BITE] = peak(CAB_BITE_HZ, CAB_BITE_DB, CAB_BITE_Q);
    filters_[CAB_NOTCH] = peak(CAB_NOTCH_HZ, CAB_NOTCH_DB, CAB_NOTCH_Q);
    filters_[CAB_LOW_PASS_1] = pass(CAB_LOW_PASS_HZ, false, CAB_LOW_PASS_Q1);
    filters_[CAB_LOW_PASS_2] = pass(CAB_LOW_PASS_HZ, false, CAB_LOW_PASS_Q2);
    for (std::size_t index = 0; index < cabinetDelays_.size(); ++index) {
        cabinetDelays_[index] = delayFrames(CABINET_DELAYS_MS[index], sampleRate, MAX_DELAY_FRAMES);
    }
    for (std::size_t index = 0; index < roomDelays_.size(); ++index) {
        roomDelays_[index] = delayFrames(ROOM_DELAYS_MS[index], sampleRate, MAX_DELAY_FRAMES - stereoOffset_);
    }
}

void Overdrive::reset() noexcept {
    states_.fill(ChannelState{});
    for (auto& history : history_) {
        history.fill(0.0f);
    }
    historyIndex_ = 0;
}

float Overdrive::run(int filter, ChannelState& state, float input) const noexcept {
    const auto& biquad = filters_[filter];
    auto& memory = state.filters[filter];
    const float output = biquad.b0 * input + memory.z1;
    memory.z1 = biquad.b1 * input - biquad.a1 * output + memory.z2;
    memory.z2 = biquad.b2 * input - biquad.a2 * output;
    return output;
}

float Overdrive::delayed(int channel, int frames) const noexcept {
    int index = historyIndex_ - frames;
    if (index < 0) index += MAX_DELAY_FRAMES;
    return history_[channel][index];
}

float Overdrive::shape(Shaper& shaper, float input, float bias, float offset) noexcept {
    const float shifted = input + bias;
    const float antiderivative = logCosh(shifted);
    const float step = shifted - shaper.lastInput;
    const float shaped = std::abs(step) > ADAA_MIN_STEP
        ? (antiderivative - shaper.lastAntiderivative) / step
        : std::tanh(0.5f * (shifted + shaper.lastInput));
    shaper.lastInput = shifted;
    shaper.lastAntiderivative = antiderivative;
    return shaped - offset;
}

void Overdrive::process(float* samples, std::size_t frames, float mix, float drive) noexcept {
    if (mix <= 0.0f && mix_ < BYPASS_MIX) {
        mix_ = 0.0f;
        bypassed_ = true;
        return;
    }

    if (bypassed_) {
        reset();
        bypassed_ = false;
    }

    for (std::size_t frame = 0; frame < frames; ++frame) {
        mix_ += (mix - mix_) * mixCoefficient_;
        for (int channel = 0; channel < channels_; ++channel) {
            float& sample = samples[frame * channels_ + channel];
            auto& state = states_[channel];
            const float dry = sample;
            state.peak = std::max(std::abs(dry), state.peak * peakRelease_);

            const float tight = run(HIGH_PASS, state, dry);
            const float voiced = run(MID_BOOST, state, tight);
            const float first = shape(state.first, voiced * drive, FIRST_BIAS, firstOffset_);
            const float coupled = run(COUPLING, state, first);
            const float second = shape(state.second, coupled * SECOND_STAGE_GAIN, SECOND_BIAS, secondOffset_);
            const float stacked = run(TREBLE, state, run(SCOOP, state, second));

            const float level = std::abs(stacked);
            state.sag += (level - state.sag) * (level > state.sag ? sagAttack_ : sagRelease_);
            const float power = shape(state.power, stacked * POWER_GAIN / (1.0f + SAG_AMOUNT * state.sag), 0.0f, 0.0f);
            float cabinet = run(PRESENCE, state, power);
            for (int filter = CAB_HIGH_PASS; filter <= CAB_LOW_PASS_2; ++filter) {
                cabinet = run(filter, state, cabinet);
            }

            history_[channel][historyIndex_] = cabinet;
            float boxed = cabinet;
            for (std::size_t tap = 0; tap < cabinetDelays_.size(); ++tap) {
                boxed += CABINET_GAINS[tap] * delayed(channel, cabinetDelays_[tap]);
            }

            float room = 0;
            const int roomOffset = channel % 2 == 1 ? stereoOffset_ : 0;
            for (std::size_t tap = 0; tap < roomDelays_.size(); ++tap) {
                room += ROOM_GAINS[tap] * delayed(channel, roomDelays_[tap] + roomOffset);
            }
            state.roomTone += (room - state.roomTone) * roomToneCoefficient_;

            const float toned = boxed + ROOM_LEVEL * state.roomTone;
            const float wet = toned - state.dcInput + dcCoefficient_ * state.dcOutput;
            state.dcInput = toned;
            state.dcOutput = wet;

            state.dryPower += (dry * dry - state.dryPower) * levelCoefficient_;
            state.wetPower += (wet * wet - state.wetPower) * levelCoefficient_;
            const float makeup = std::min(MAX_MAKEUP,
                std::sqrt((state.dryPower + POWER_FLOOR) / (state.wetPower + POWER_FLOOR)));
            state.makeup += (makeup - state.makeup) * levelCoefficient_;
            const float mixed = dry + (wet * state.makeup - dry) * mix_;

            state.dryLoudness += (dry * dry - state.dryLoudness) * loudnessCoefficient_;
            state.outputLoudness += (mixed * mixed - state.outputLoudness) * loudnessCoefficient_;
            const float ceiling = std::min(1.0f,
                std::sqrt((state.dryLoudness + POWER_FLOOR) / (state.outputLoudness + POWER_FLOOR)));
            state.ceilingGain += (ceiling - state.ceilingGain)
                * (ceiling < state.ceilingGain ? ceilingAttack_ : ceilingRelease_);

            sample = softLimit(mixed * state.ceilingGain, state.peak);
        }

        historyIndex_ = (historyIndex_ + 1) % MAX_DELAY_FRAMES;
    }
}

void YARG_BASS_CALLBACK overdriveDspProc(std::uint32_t, std::uint32_t,
    void* buffer, std::uint32_t length, void* user) noexcept {
    if (!buffer || !user || length == 0 || length % sizeof(float) != 0) return;

    auto* state = static_cast<yarg_overdrive_dsp*>(user);
    const float mix = bitCast<float>(state->mixBits.load(std::memory_order_relaxed));
    const float drive = bitCast<float>(state->driveBits.load(std::memory_order_relaxed));
    const std::size_t frames = length / sizeof(float) / static_cast<std::size_t>(state->channelCount);
    state->overdrive.process(static_cast<float*>(buffer), frames, mix, drive);
}

int overdriveDspAttach(const BassCoreBindings& bass, std::uint32_t channel, float mix, float drive,
    int priority, yarg_overdrive_dsp** dsp, int* bassError) noexcept {
    if (dsp) *dsp = nullptr;
    if (bassError) *bassError = 0;
    if (!dsp || channel == 0 || !std::isfinite(mix) || !std::isfinite(drive) || mix < 0.0f || mix > 1.0f ||
        drive <= 0.0f || drive > Overdrive::MAX_DRIVE) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    if (!bass.valid()) return YARG_AUDIO_ERROR_DEPENDENCY;

    BassChannelInfo info{};
    if (!bass.getChannelInfo(channel, info)) {
        if (bassError) *bassError = bass.error();
        return YARG_AUDIO_ERROR_BASS;
    }
    if (info.frequency == 0 || info.channels == 0 || info.channels > Overdrive::MAX_CHANNELS) {
        return YARG_AUDIO_ERROR_INVALID_STATE;
    }
    if ((info.flags & BassSampleFloat) == 0 && bass.getConfig(BassConfigFloatDsp) == 0) {
        return YARG_AUDIO_ERROR_UNSUPPORTED;
    }

    auto* state = new (std::nothrow) yarg_overdrive_dsp(bass, channel, static_cast<int>(info.channels),
        static_cast<float>(info.frequency), mix, drive);
    if (!state) return YARG_AUDIO_ERROR_INTERNAL;

    state->dsp = bass.setDsp(channel, &overdriveDspProc, state, priority);
    if (state->dsp == 0) {
        if (bassError) *bassError = bass.error();
        delete state;
        return YARG_AUDIO_ERROR_BASS;
    }

    *dsp = state;
    return YARG_AUDIO_OK;
}

int overdriveDspSet(yarg_overdrive_dsp* dsp, float mix, float drive) noexcept {
    if (!dsp || !std::isfinite(mix) || !std::isfinite(drive) || mix < 0.0f || mix > 1.0f ||
        drive <= 0.0f || drive > Overdrive::MAX_DRIVE) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    dsp->mixBits.store(bitCast<std::uint32_t>(mix), std::memory_order_relaxed);
    dsp->driveBits.store(bitCast<std::uint32_t>(drive), std::memory_order_relaxed);
    return YARG_AUDIO_OK;
}

bool overdriveDspDestroy(yarg_overdrive_dsp* dsp) noexcept {
    if (!dsp) return true;
    if (!dsp->bass.lockChannel(dsp->channel, true)) return false;

    const bool removed = dsp->bass.removeDsp(dsp->channel, dsp->dsp);
    dsp->bass.lockChannel(dsp->channel, false);
    if (!removed) return false;

    delete dsp;
    return true;
}

}

yarg_overdrive_dsp::yarg_overdrive_dsp(const yarg::audio::BassCoreBindings& bindings, std::uint32_t channelHandle,
    int channels, float sampleRate, float mix, float drive) noexcept
    : bass(bindings), channel(channelHandle), channelCount(channels),
      mixBits(yarg::audio::bitCast<std::uint32_t>(mix)),
      driveBits(yarg::audio::bitCast<std::uint32_t>(drive)),
      overdrive(channels, sampleRate) {}
