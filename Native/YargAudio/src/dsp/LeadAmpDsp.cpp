#include "dsp/LeadAmpDsp.h"

#include "BitCastCompat.h"

#include <algorithm>
#include <cmath>
#include <new>

static_assert(std::atomic<std::uint32_t>::is_always_lock_free);

namespace {

constexpr std::uint32_t BassSampleFloat = 0x100;
constexpr std::uint32_t BassConfigFloatDsp = 9;

constexpr double PI = 3.14159265358979323846;
constexpr float BUTTERWORTH_Q = 0.7071f;
constexpr float PRE_HIGH_PASS_HZ = 100.0f;
constexpr float PRE_BOOST_HZ = 800.0f;
constexpr float PRE_BOOST_DB = 7.0f;
constexpr float PRE_BOOST_Q = 0.7f;
constexpr float PRE_LOW_PASS_HZ = 6000.0f;
constexpr float INPUT_GAIN = 15.811f;
constexpr float FIRST_BIAS = 0.15f;
constexpr float COUPLING_HZ = 120.0f;
constexpr float INTERSTAGE_LOW_PASS_HZ = 9000.0f;
constexpr float SECOND_GAIN = 3.0f;
constexpr float SECOND_BIAS = 0.05f;
constexpr float LOW_SHELF_HZ = 300.0f;
constexpr float LOW_SHELF_DB = 5.0f;
constexpr float MID_CUT_HZ = 500.0f;
constexpr float MID_CUT_DB = -1.0f;
constexpr float MID_CUT_Q = 0.8f;
constexpr float PRESENCE_HZ = 2200.0f;
constexpr float PRESENCE_DB = 3.5f;
constexpr float PRESENCE_Q = 1.0f;
constexpr float CAB_HIGH_PASS_HZ = 80.0f;
constexpr float CAB_THUMP_HZ = 115.0f;
constexpr float CAB_THUMP_DB = 4.0f;
constexpr float CAB_THUMP_Q = 1.2f;
constexpr float CAB_BODY_HZ = 220.0f;
constexpr float CAB_BODY_DB = 3.0f;
constexpr float CAB_BODY_Q = 1.0f;
constexpr float CAB_BITE_HZ = 2300.0f;
constexpr float CAB_BITE_DB = 4.0f;
constexpr float CAB_BITE_Q = 1.4f;
constexpr float CAB_HONK_HZ = 3600.0f;
constexpr float CAB_HONK_DB = -2.0f;
constexpr float CAB_HONK_Q = 2.0f;
constexpr float CAB_LOW_PASS_HZ = 4800.0f;
constexpr float CAB_LOW_PASS_Q1 = 0.5412f;
constexpr float CAB_LOW_PASS_Q2 = 1.3066f;
constexpr float ECHO_SECONDS[] = {0.37f, 0.41f};
constexpr float ECHO_FEEDBACK = 0.32f;
constexpr float ECHO_LEVEL = 0.35f;
constexpr float ECHO_TONE_HZ = 3500.0f;
constexpr float GAIN_SMOOTHING_MS = 20.0f;
constexpr float ADAA_MIN_STEP = 1e-4f;
constexpr int CABINET_FADE_FRAMES = 64;
constexpr int CABINET_LANES = 4;
constexpr float HISS_LEVEL = 0.0003f;
constexpr float HUM_LEVEL = 0.0004f;
constexpr float HUM_HZ = 60.0f;
constexpr int HUM_HARMONICS = 12;
constexpr float NOISE_OPEN_LEVEL = 0.02f;
constexpr float NOISE_CLOSED_LEVEL = 0.001f;
constexpr float NOISE_RELEASE_SECONDS = 0.5f;

float logCosh(float value) noexcept {
    const float magnitude = std::abs(value);
    return magnitude + std::log1p(std::exp(-2.0f * magnitude)) - 0.69314718f;
}

float onePole(float hz, float sampleRate) noexcept {
    return static_cast<float>(1.0 - std::exp(-2.0 * PI * hz / sampleRate));
}

}

namespace yarg::audio {

LeadAmp::LeadAmp(int channels, float sampleRate, const float* cabinet, std::size_t cabinetFrames,
    float cabinetRate)
    : channels_(std::clamp(channels, 1, MAX_CHANNELS)),
      couplingCoefficient_(static_cast<float>(std::exp(-2.0 * PI * COUPLING_HZ / sampleRate))),
      interstageCoefficient_(onePole(std::min(INTERSTAGE_LOW_PASS_HZ, 0.45f * sampleRate), sampleRate)),
      echoToneCoefficient_(onePole(ECHO_TONE_HZ, sampleRate)),
      gainCoefficient_(static_cast<float>(1.0 - std::exp(-1.0 / (GAIN_SMOOTHING_MS * 0.001 * sampleRate)))),
      firstOffset_(std::tanh(FIRST_BIAS)),
      secondOffset_(std::tanh(SECOND_BIAS)),
      noiseReleaseCoefficient_(static_cast<float>(std::exp(-1.0 / (NOISE_RELEASE_SECONDS * sampleRate)))) {
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
    const auto lowShelf = [&angle](float hz, float gainDb, float q) {
        const double w0 = angle(hz);
        const double cosine = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        const double amplitude = std::pow(10.0, gainDb / 40.0);
        const double root = 2.0 * std::sqrt(amplitude) * alpha;
        const double a0 = (amplitude + 1.0) + (amplitude - 1.0) * cosine + root;
        Biquad biquad;
        biquad.b0 = static_cast<float>(amplitude * ((amplitude + 1.0) - (amplitude - 1.0) * cosine + root) / a0);
        biquad.b1 = static_cast<float>(2.0 * amplitude * ((amplitude - 1.0) - (amplitude + 1.0) * cosine) / a0);
        biquad.b2 = static_cast<float>(amplitude * ((amplitude + 1.0) - (amplitude - 1.0) * cosine - root) / a0);
        biquad.a1 = static_cast<float>(-2.0 * ((amplitude - 1.0) + (amplitude + 1.0) * cosine) / a0);
        biquad.a2 = static_cast<float>(((amplitude + 1.0) + (amplitude - 1.0) * cosine - root) / a0);
        return biquad;
    };
    filters_[PRE_HIGH_PASS] = pass(PRE_HIGH_PASS_HZ, true, BUTTERWORTH_Q);
    filters_[PRE_BOOST] = peak(PRE_BOOST_HZ, PRE_BOOST_DB, PRE_BOOST_Q);
    filters_[PRE_LOW_PASS] = pass(PRE_LOW_PASS_HZ, false, BUTTERWORTH_Q);
    filters_[LOW_SHELF] = lowShelf(LOW_SHELF_HZ, LOW_SHELF_DB, BUTTERWORTH_Q);
    filters_[MID_CUT] = peak(MID_CUT_HZ, MID_CUT_DB, MID_CUT_Q);
    filters_[PRESENCE] = peak(PRESENCE_HZ, PRESENCE_DB, PRESENCE_Q);
    filters_[CAB_HIGH_PASS] = pass(CAB_HIGH_PASS_HZ, true, BUTTERWORTH_Q);
    filters_[CAB_THUMP] = peak(CAB_THUMP_HZ, CAB_THUMP_DB, CAB_THUMP_Q);
    filters_[CAB_BODY] = peak(CAB_BODY_HZ, CAB_BODY_DB, CAB_BODY_Q);
    filters_[CAB_BITE] = peak(CAB_BITE_HZ, CAB_BITE_DB, CAB_BITE_Q);
    filters_[CAB_HONK] = peak(CAB_HONK_HZ, CAB_HONK_DB, CAB_HONK_Q);
    filters_[CAB_LOW_PASS_1] = pass(CAB_LOW_PASS_HZ, false, CAB_LOW_PASS_Q1);
    filters_[CAB_LOW_PASS_2] = pass(CAB_LOW_PASS_HZ, false, CAB_LOW_PASS_Q2);

    echoLength_ = static_cast<int>(std::ceil(ECHO_SECONDS[1] * sampleRate)) + 1;
    for (int channel = 0; channel < channels_; ++channel) {
        auto& state = states_[channel];
        state.first = Shaper{FIRST_BIAS, logCosh(FIRST_BIAS)};
        state.second = Shaper{SECOND_BIAS, logCosh(SECOND_BIAS)};
        state.echo.assign(static_cast<std::size_t>(echoLength_), 0.0f);
        state.echoFrames = std::clamp(static_cast<int>(std::lround(ECHO_SECONDS[channel % 2] * sampleRate)),
            1, echoLength_ - 1);
    }

    hum_.assign(static_cast<std::size_t>(std::max(1L, std::lround(sampleRate / HUM_HZ))), 0.0f);
    float humPeak = 0.0f;
    for (std::size_t frame = 0; frame < hum_.size(); ++frame) {
        double value = 0.0;
        for (int harmonic = 1; harmonic <= HUM_HARMONICS; ++harmonic) {
            value += std::sin(2.0 * PI * harmonic * frame / hum_.size() + 0.7 * harmonic * harmonic) / harmonic;
        }
        hum_[frame] = static_cast<float>(value);
        humPeak = std::max(humPeak, std::abs(hum_[frame]));
    }
    for (float& sample : hum_) {
        sample /= humPeak;
    }

    loadCabinet(cabinet, cabinetFrames, cabinetRate, sampleRate);
    for (int channel = 0; channel < channels_; ++channel) {
        states_[channel].cabinetHistory.assign(cabinet_.size() * 2, 0.0f);
    }
}

void LeadAmp::loadCabinet(const float* cabinet, std::size_t frames, float cabinetRate, float sampleRate) {
    if (!cabinet || frames == 0 || !std::isfinite(cabinetRate) || cabinetRate <= 0.0f) return;

    const double step = static_cast<double>(cabinetRate) / sampleRate;
    const auto resampledFrames = std::min<std::size_t>(MAX_CABINET_FRAMES,
        static_cast<std::size_t>((frames - 1) / step) + 1);
    const std::size_t paddedFrames = (resampledFrames + CABINET_LANES - 1) / CABINET_LANES * CABINET_LANES;
    std::vector<float> resampled(paddedFrames, 0.0f);
    for (std::size_t frame = 0; frame < resampledFrames; ++frame) {
        const double position = frame * step;
        const auto index = std::min(static_cast<std::size_t>(position), frames - 1);
        const auto next = std::min(index + 1, frames - 1);
        const auto fraction = static_cast<float>(position - index);
        resampled[frame] = cabinet[index] + fraction * (cabinet[next] - cabinet[index]);
    }

    const std::size_t fadeFrames = std::min<std::size_t>(CABINET_FADE_FRAMES, resampledFrames / 4);
    for (std::size_t frame = 0; frame < fadeFrames; ++frame) {
        resampled[resampledFrames - 1 - frame] *= static_cast<float>(frame + 1) / (fadeFrames + 1);
    }

    double energy = 0.0;
    for (float sample : resampled) {
        energy += static_cast<double>(sample) * sample;
    }
    if (!std::isfinite(energy) || energy <= 0.0) return;

    const auto scale = static_cast<float>(1.0 / std::sqrt(energy));
    for (float& sample : resampled) {
        sample *= scale;
    }
    cabinet_ = std::move(resampled);
}

float LeadAmp::run(int filter, ChannelState& state, float input) const noexcept {
    const auto& biquad = filters_[filter];
    auto& memory = state.filters[filter];
    const float output = biquad.b0 * input + memory.z1;
    memory.z1 = biquad.b1 * input - biquad.a1 * output + memory.z2;
    memory.z2 = biquad.b2 * input - biquad.a2 * output;
    return output;
}

float LeadAmp::convolve(ChannelState& state, float input) const noexcept {
    const std::size_t length = cabinet_.size();
    state.cabinetHistory[cabinetIndex_] = input;
    state.cabinetHistory[cabinetIndex_ + length] = input;

    const float* recent = state.cabinetHistory.data() + cabinetIndex_;
    const float* taps = cabinet_.data();
    float sums[CABINET_LANES] = {};
    for (std::size_t tap = 0; tap < length; tap += CABINET_LANES) {
        for (int lane = 0; lane < CABINET_LANES; ++lane) {
            sums[lane] += taps[tap + lane] * recent[tap + lane];
        }
    }
    return (sums[0] + sums[1]) + (sums[2] + sums[3]);
}

float LeadAmp::noise(const float* frame) noexcept {
    float loudest = 0.0f;
    for (int channel = 0; channel < channels_; ++channel) {
        loudest = std::max(loudest, std::abs(frame[channel]));
    }
    noiseEnvelope_ = std::max(loudest, noiseEnvelope_ * noiseReleaseCoefficient_);
    const float hum = hum_[humIndex_];
    humIndex_ = (humIndex_ + 1) % hum_.size();

    hissState_ ^= hissState_ << 13;
    hissState_ ^= hissState_ >> 17;
    hissState_ ^= hissState_ << 5;
    const float hiss = static_cast<float>(hissState_) / 2147483648.0f - 1.0f;

    const float open = std::clamp((noiseEnvelope_ - NOISE_CLOSED_LEVEL) / (NOISE_OPEN_LEVEL - NOISE_CLOSED_LEVEL),
        0.0f, 1.0f);
    return open * (HISS_LEVEL * hiss + HUM_LEVEL * hum);
}

float LeadAmp::shape(Shaper& shaper, float input, float bias, float offset) noexcept {
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

void LeadAmp::process(float* samples, std::size_t frames, float gain) noexcept {
    if (gain_ < 0.0f) gain_ = gain;
    for (std::size_t frame = 0; frame < frames; ++frame) {
        gain_ += (gain - gain_) * gainCoefficient_;
        const float pickupNoise = noise(samples + frame * channels_);
        for (int channel = 0; channel < channels_; ++channel) {
            float& sample = samples[frame * channels_ + channel];
            auto& state = states_[channel];

            float signal = run(PRE_HIGH_PASS, state, sample + pickupNoise);
            signal = run(PRE_BOOST, state, signal);
            signal = run(PRE_LOW_PASS, state, signal);
            const float first = shape(state.first, signal * INPUT_GAIN, FIRST_BIAS, firstOffset_);

            const float coupled = first - state.couplingInput + couplingCoefficient_ * state.couplingOutput;
            state.couplingInput = first;
            state.couplingOutput = coupled;
            state.interstage += (coupled - state.interstage) * interstageCoefficient_;
            const float second = shape(state.second, state.interstage * SECOND_GAIN, SECOND_BIAS, secondOffset_);

            float amp = run(LOW_SHELF, state, second);
            amp = run(MID_CUT, state, amp);
            amp = run(PRESENCE, state, amp);
            if (cabinet_.empty()) {
                for (int filter = CAB_HIGH_PASS; filter <= CAB_LOW_PASS_2; ++filter) {
                    amp = run(filter, state, amp);
                }
            } else {
                amp = convolve(state, amp);
            }

            int read = echoIndex_ - state.echoFrames;
            if (read < 0) read += echoLength_;
            const float echo = state.echo[static_cast<std::size_t>(read)];
            state.echoTone += (amp - state.echoTone) * echoToneCoefficient_;
            state.echo[static_cast<std::size_t>(echoIndex_)] = ECHO_FEEDBACK * (state.echoTone + echo);

            sample = (amp + ECHO_LEVEL * echo) * gain_;
        }

        echoIndex_ = (echoIndex_ + 1) % echoLength_;
        if (!cabinet_.empty()) {
            cabinetIndex_ = (cabinetIndex_ == 0 ? static_cast<int>(cabinet_.size()) : cabinetIndex_) - 1;
        }
    }
}

void YARG_BASS_CALLBACK leadAmpDspProc(std::uint32_t, std::uint32_t,
    void* buffer, std::uint32_t length, void* user) noexcept {
    if (!buffer || !user || length == 0 || length % sizeof(float) != 0) return;

    auto* state = static_cast<yarg_lead_amp_dsp*>(user);
    const float gain = bitCast<float>(state->gainBits.load(std::memory_order_relaxed));
    const std::size_t frames = length / sizeof(float) / static_cast<std::size_t>(state->channelCount);
    state->amp.process(static_cast<float*>(buffer), frames, gain);
}

int leadAmpDspAttach(const BassCoreBindings& bass, std::uint32_t channel, float gain,
    const float* cabinet, std::size_t cabinetFrames, float cabinetRate,
    int priority, yarg_lead_amp_dsp** dsp, int* bassError) noexcept {
    if (dsp) *dsp = nullptr;
    if (bassError) *bassError = 0;
    if (!dsp || channel == 0 || !std::isfinite(gain) || gain < 0.0f || gain > LeadAmp::MAX_GAIN) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    if (cabinetFrames > 0 && (!cabinet || !std::isfinite(cabinetRate) || cabinetRate <= 0.0f)) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    if (!bass.valid()) return YARG_AUDIO_ERROR_DEPENDENCY;

    BassChannelInfo info{};
    if (!bass.getChannelInfo(channel, info)) {
        if (bassError) *bassError = bass.error();
        return YARG_AUDIO_ERROR_BASS;
    }
    if (info.frequency == 0 || info.channels == 0 || info.channels > LeadAmp::MAX_CHANNELS) {
        return YARG_AUDIO_ERROR_INVALID_STATE;
    }
    if ((info.flags & BassSampleFloat) == 0 && bass.getConfig(BassConfigFloatDsp) == 0) {
        return YARG_AUDIO_ERROR_UNSUPPORTED;
    }

    yarg_lead_amp_dsp* state = nullptr;
    try {
        state = new yarg_lead_amp_dsp(bass, channel, static_cast<int>(info.channels),
            static_cast<float>(info.frequency), gain, cabinet, cabinetFrames, cabinetRate);
    } catch (const std::bad_alloc&) {
        return YARG_AUDIO_ERROR_INTERNAL;
    }

    state->dsp = bass.setDsp(channel, &leadAmpDspProc, state, priority);
    if (state->dsp == 0) {
        if (bassError) *bassError = bass.error();
        delete state;
        return YARG_AUDIO_ERROR_BASS;
    }

    *dsp = state;
    return YARG_AUDIO_OK;
}

int leadAmpDspSetGain(yarg_lead_amp_dsp* dsp, float gain) noexcept {
    if (!dsp || !std::isfinite(gain) || gain < 0.0f || gain > LeadAmp::MAX_GAIN) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    dsp->gainBits.store(bitCast<std::uint32_t>(gain), std::memory_order_relaxed);
    return YARG_AUDIO_OK;
}

bool leadAmpDspDestroy(yarg_lead_amp_dsp* dsp) noexcept {
    if (!dsp) return true;
    if (!dsp->bass.lockChannel(dsp->channel, true)) return false;

    const bool removed = dsp->bass.removeDsp(dsp->channel, dsp->dsp);
    dsp->bass.lockChannel(dsp->channel, false);
    if (!removed) return false;

    delete dsp;
    return true;
}

}

yarg_lead_amp_dsp::yarg_lead_amp_dsp(const yarg::audio::BassCoreBindings& bindings, std::uint32_t channelHandle,
    int channels, float sampleRate, float gain, const float* cabinet, std::size_t cabinetFrames,
    float cabinetRate)
    : bass(bindings), channel(channelHandle), channelCount(channels),
      gainBits(yarg::audio::bitCast<std::uint32_t>(gain)),
      amp(channels, sampleRate, cabinet, cabinetFrames, cabinetRate) {}
