#include "dsp/LeadAmpDsp.h"

#include "BitCastCompat.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <iterator>
#include <new>
#include <vector>

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
constexpr float TONE_Q = 1.41f;
constexpr float MATCH_DRIVES[] = {0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f};
constexpr float MATCH_ATTACK_SECONDS = 0.05f;
constexpr float MATCH_WINDOW_SECONDS = 0.005f;
constexpr float MATCH_BODY_SECONDS = 0.08f;
constexpr float MATCH_LEVEL_SECONDS = 0.3f;
constexpr float MATCH_MIN_NOTE_SECONDS = 0.2f;
constexpr float MATCH_MAX_NOTE_SECONDS = 0.35f;
constexpr int MATCH_MIN_NOTES = 3;
constexpr int MATCH_TONE_PASSES = 24;
constexpr double MATCH_BAND_FLOOR = 1e-5;
constexpr std::size_t MATCH_RENDER_BLOCK = 512;

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
      sampleRate_(sampleRate),
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
    const auto peak = [sampleRate](float hz, float gainDb, float q) {
        return LeadAmp::peak(sampleRate, hz, gainDb, q);
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
    for (int band = 0; band < TONE_BANDS; ++band) {
        filters_[TONE_FIRST + band] = toneBand(sampleRate, band, 0.0f);
    }

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

LeadAmp::Biquad LeadAmp::peak(float sampleRate, float hz, float gainDb, float q) noexcept {
    const double w0 = 2.0 * PI * std::min(hz, 0.45f * sampleRate) / sampleRate;
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
}

float LeadAmp::toneHz(int band) noexcept {
    return TONE_LOWEST_HZ * static_cast<float>(1 << band);
}

LeadAmp::Biquad LeadAmp::toneBand(float sampleRate, int band, float gainDb) noexcept {
    return peak(sampleRate, toneHz(band), gainDb, TONE_Q);
}

bool LeadAmp::validTone(float drive, const float* bandsDb) noexcept {
    if (!bandsDb || !std::isfinite(drive) || drive < MIN_DRIVE || drive > MAX_DRIVE) return false;
    for (int band = 0; band < TONE_BANDS; ++band) {
        if (!std::isfinite(bandsDb[band]) || std::abs(bandsDb[band]) > MAX_TONE_DB) return false;
    }
    return true;
}

void LeadAmp::setTone(float drive, const float* bandsDb) noexcept {
    targetDrive_ = drive;
    for (int band = 0; band < TONE_BANDS; ++band) {
        filters_[TONE_FIRST + band] = toneBand(sampleRate_, band, bandsDb[band]);
    }
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
    if (drive_ < 0.0f) drive_ = targetDrive_;
    for (std::size_t frame = 0; frame < frames; ++frame) {
        gain_ += (gain - gain_) * gainCoefficient_;
        drive_ += (targetDrive_ - drive_) * gainCoefficient_;
        const float pickupNoise = noise(samples + frame * channels_);
        for (int channel = 0; channel < channels_; ++channel) {
            float& sample = samples[frame * channels_ + channel];
            auto& state = states_[channel];

            float signal = run(PRE_HIGH_PASS, state, sample + pickupNoise);
            signal = run(PRE_BOOST, state, signal);
            signal = run(PRE_LOW_PASS, state, signal);
            const float first = shape(state.first, signal * INPUT_GAIN * drive_, FIRST_BIAS, firstOffset_);

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
            for (int band = TONE_FIRST; band < FILTER_COUNT; ++band) {
                amp = run(band, state, amp);
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
    const std::uint32_t toneVersion = state->toneVersion.load(std::memory_order_acquire);
    if (toneVersion != state->appliedToneVersion) {
        std::array<float, LeadAmp::TONE_BANDS> bands{};
        for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
            bands[band] = bitCast<float>(state->toneBits[band].load(std::memory_order_relaxed));
        }
        state->amp.setTone(bitCast<float>(state->driveBits.load(std::memory_order_relaxed)), bands.data());
        state->appliedToneVersion = toneVersion;
    }
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

int leadAmpDspSetTone(yarg_lead_amp_dsp* dsp, float drive, const float* bandsDb, std::size_t bandCount) noexcept {
    if (!dsp || bandCount != LeadAmp::TONE_BANDS || !LeadAmp::validTone(drive, bandsDb)) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    dsp->driveBits.store(bitCast<std::uint32_t>(drive), std::memory_order_relaxed);
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        dsp->toneBits[band].store(bitCast<std::uint32_t>(bandsDb[band]), std::memory_order_relaxed);
    }
    dsp->toneVersion.fetch_add(1, std::memory_order_release);
    return YARG_AUDIO_OK;
}

namespace {

struct MatchNote {
    const float* stem;
    const float* di;
    std::size_t frames;
};

struct ToneMeasure {
    std::array<double, LeadAmp::TONE_BANDS> bands{};
    double punch = 0.0;
    double level = 0.0;
};

double median(std::vector<double> values) {
    if (values.empty()) return 0.0;
    const auto middle = values.begin() + static_cast<std::ptrdiff_t>(values.size() / 2);
    std::nth_element(values.begin(), middle, values.end());
    return *middle;
}

double rms(const float* samples, std::size_t first, std::size_t end) {
    if (end <= first) return 0.0;
    double energy = 0.0;
    for (std::size_t frame = first; frame < end; ++frame) {
        energy += static_cast<double>(samples[frame]) * samples[frame];
    }
    return std::sqrt(energy / static_cast<double>(end - first));
}

double bandEnergy(const float* samples, std::size_t frames, float sampleRate, float hz) {
    const double w0 = 2.0 * PI * std::min(hz, 0.45f * sampleRate) / sampleRate;
    const double alpha = std::sin(w0) / (2.0 * TONE_Q);
    const double a0 = 1.0 + alpha;
    const double b0 = alpha / a0;
    const double a1 = -2.0 * std::cos(w0) / a0;
    const double a2 = (1.0 - alpha) / a0;
    double z1 = 0.0, z2 = 0.0, energy = 0.0;
    for (std::size_t frame = 0; frame < frames; ++frame) {
        const double input = samples[frame];
        const double output = b0 * input + z1;
        z1 = -a1 * output + z2;
        z2 = -b0 * input - a2 * output;
        energy += output * output;
    }
    return energy;
}

ToneMeasure measure(const std::vector<std::vector<float>>& notes, float sampleRate) {
    const auto frames = [sampleRate](float seconds) {
        return static_cast<std::size_t>(seconds * sampleRate);
    };
    const std::size_t window = std::max<std::size_t>(1, frames(MATCH_WINDOW_SECONDS));
    ToneMeasure tone;
    std::vector<double> punches, levels;
    int spectra = 0;
    for (const auto& note : notes) {
        const float* samples = note.data();
        const std::size_t length = note.size();
        double attackPeak = 0.0;
        for (std::size_t first = 0; first + window <= std::min(length, frames(MATCH_ATTACK_SECONDS)); first += window) {
            attackPeak = std::max(attackPeak, rms(samples, first, first + window));
        }
        const double body = rms(samples, std::min(length, frames(MATCH_BODY_SECONDS)), length);
        if (body > 0.0) punches.push_back(attackPeak / body);
        levels.push_back(rms(samples, 0, std::min(length, frames(MATCH_LEVEL_SECONDS))));

        std::array<double, LeadAmp::TONE_BANDS> bands{};
        double total = 0.0;
        for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
            bands[band] = bandEnergy(samples, length, sampleRate, LeadAmp::toneHz(band));
            total += bands[band];
        }
        if (!(total > 0.0)) continue;
        for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
            tone.bands[band] += bands[band] / total;
        }
        ++spectra;
    }
    for (double& band : tone.bands) {
        band /= std::max(spectra, 1);
    }
    tone.punch = median(std::move(punches));
    tone.level = median(std::move(levels));
    return tone;
}

std::vector<std::vector<float>> render(const std::vector<MatchNote>& notes, float sampleRate, const float* cabinet,
    std::size_t cabinetFrames, float cabinetRate, float drive) {
    const std::array<float, LeadAmp::TONE_BANDS> flat{};
    std::vector<std::vector<float>> rendered;
    rendered.reserve(notes.size());
    for (const auto& note : notes) {
        LeadAmp amp(1, sampleRate, cabinet, cabinetFrames, cabinetRate);
        amp.setTone(drive, flat.data());
        std::vector<float> samples(note.di, note.di + note.frames);
        for (std::size_t first = 0; first < samples.size(); first += MATCH_RENDER_BLOCK) {
            amp.process(samples.data() + first, std::min(MATCH_RENDER_BLOCK, samples.size() - first),
                1.0f);
        }
        rendered.push_back(std::move(samples));
    }
    return rendered;
}

std::vector<std::vector<float>> equalize(std::vector<std::vector<float>> notes, float sampleRate,
    const std::array<float, LeadAmp::TONE_BANDS>& bandsDb) {
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        const auto filter = LeadAmp::toneBand(sampleRate, band, bandsDb[band]);
        for (auto& note : notes) {
            float z1 = 0.0f, z2 = 0.0f;
            for (float& sample : note) {
                const float output = filter.b0 * sample + z1;
                z1 = filter.b1 * sample - filter.a1 * output + z2;
                z2 = filter.b2 * sample - filter.a2 * output;
                sample = output;
            }
        }
    }
    return notes;
}

float fitDrive(double targetPunch, const std::array<double, std::size(MATCH_DRIVES)>& punches) {
    const double target = std::log(targetPunch);
    std::size_t nearest = 0;
    for (std::size_t index = 0; index < punches.size(); ++index) {
        if (std::abs(std::log(punches[index]) - target) < std::abs(std::log(punches[nearest]) - target)) {
            nearest = index;
        }
    }
    for (std::size_t index = 0; index + 1 < punches.size(); ++index) {
        const double low = std::log(punches[index]);
        const double high = std::log(punches[index + 1]);
        if (low == high || (target - low) * (target - high) > 0.0) continue;
        const double position = (target - low) / (high - low);
        const double logDrive = std::log(MATCH_DRIVES[index])
            + position * (std::log(MATCH_DRIVES[index + 1]) - std::log(MATCH_DRIVES[index]));
        return static_cast<float>(std::exp(logDrive));
    }
    return MATCH_DRIVES[nearest];
}

void correct(std::array<float, LeadAmp::TONE_BANDS>& bandsDb, const ToneMeasure& stem, const ToneMeasure& amp) {
    double weighted = 0.0, weight = 0.0;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        if (stem.bands[band] > MATCH_BAND_FLOOR && amp.bands[band] > MATCH_BAND_FLOOR) {
            bandsDb[band] += static_cast<float>(10.0 * std::log10(stem.bands[band] / amp.bands[band]));
        }
        weighted += stem.bands[band] * bandsDb[band];
        weight += stem.bands[band];
    }
    const auto centre = static_cast<float>(weight > 0.0 ? weighted / weight : 0.0);
    for (float& band : bandsDb) {
        band = std::clamp(band - centre, -LeadAmp::MAX_TONE_DB, LeadAmp::MAX_TONE_DB);
    }
}

}

int leadAmpMatch(float sampleRate, const float* cabinet, std::size_t cabinetFrames, float cabinetRate,
    const float* stem, const float* di, const std::uint32_t* noteFrames, std::size_t noteCount,
    float* drive, float* bandsDb, std::size_t bandCount, float* levelGain) noexcept {
    if (!drive || !bandsDb || !levelGain || bandCount != LeadAmp::TONE_BANDS) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    *drive = 1.0f;
    *levelGain = 1.0f;
    std::fill(bandsDb, bandsDb + bandCount, 0.0f);
    if (!stem || !di || !noteFrames || !std::isfinite(sampleRate) || sampleRate <= 0.0f) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    if (cabinetFrames > 0 && (!cabinet || !std::isfinite(cabinetRate) || cabinetRate <= 0.0f)) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }

    try {
        const auto minFrames = static_cast<std::size_t>(MATCH_MIN_NOTE_SECONDS * sampleRate);
        const auto maxFrames = static_cast<std::size_t>(MATCH_MAX_NOTE_SECONDS * sampleRate);
        std::vector<MatchNote> notes;
        std::vector<std::vector<float>> stemNotes;
        std::size_t offset = 0;
        for (std::size_t index = 0; index < noteCount; ++index) {
            if (noteFrames[index] >= minFrames) {
                const std::size_t frames = std::min<std::size_t>(noteFrames[index], maxFrames);
                notes.push_back({stem + offset, di + offset, frames});
                stemNotes.emplace_back(stem + offset, stem + offset + frames);
            }
            offset += noteFrames[index];
        }
        if (notes.size() < MATCH_MIN_NOTES) return YARG_AUDIO_ERROR_INVALID_STATE;

        const auto target = measure(stemNotes, sampleRate);
        if (!(target.punch > 0.0)) return YARG_AUDIO_ERROR_INVALID_STATE;

        std::array<float, LeadAmp::TONE_BANDS> tone{};
        std::array<double, std::size(MATCH_DRIVES)> punches{};
        double defaultLevel = 0.0;
        for (std::size_t index = 0; index < punches.size(); ++index) {
            const auto rendered = measure(render(notes, sampleRate, cabinet, cabinetFrames, cabinetRate,
                MATCH_DRIVES[index]), sampleRate);
            if (!(rendered.punch > 0.0)) return YARG_AUDIO_ERROR_INVALID_STATE;
            punches[index] = rendered.punch;
            if (MATCH_DRIVES[index] == 1.0f) defaultLevel = rendered.level;
        }

        const float fitted = std::clamp(fitDrive(target.punch, punches), LeadAmp::MIN_DRIVE, LeadAmp::MAX_DRIVE);
        const auto amped = render(notes, sampleRate, cabinet, cabinetFrames, cabinetRate, fitted);
        auto rendered = measure(amped, sampleRate);
        for (int pass = 0; pass < MATCH_TONE_PASSES; ++pass) {
            correct(tone, target, rendered);
            rendered = measure(equalize(amped, sampleRate, tone), sampleRate);
        }

        *drive = fitted;
        std::copy(tone.begin(), tone.end(), bandsDb);
        *levelGain = rendered.level > 0.0 && defaultLevel > 0.0
            ? static_cast<float>(defaultLevel / rendered.level)
            : 1.0f;
        return YARG_AUDIO_OK;
    } catch (const std::bad_alloc&) {
        return YARG_AUDIO_ERROR_INTERNAL;
    }
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
      driveBits(yarg::audio::bitCast<std::uint32_t>(1.0f)),
      amp(channels, sampleRate, cabinet, cabinetFrames, cabinetRate) {}
