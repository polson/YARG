#include "dsp/LeadAmpDsp.h"

#include "BitCastCompat.h"
#include "dsp/Loudness.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iterator>
#include <new>
#include <optional>
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
constexpr float DEFAULT_ECHO_SECONDS = 0.37f;
constexpr float ECHO_SPREAD = 0.41f / 0.37f;
constexpr float ECHO_FEEDBACK = 0.32f;
constexpr float DEFAULT_ECHO_LEVEL = 0.35f;
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
constexpr float TONE_Q = 4.32f;
constexpr float WAH_Q = 4.0f;
constexpr float WAH_DRY = 0.15f;
constexpr float WAH_PEAK_GAIN = 2.0f;
constexpr float WAH_SWEEP_SECONDS = 0.12f;
constexpr float WAH_FAST_RELEASE_SECONDS = 0.01f;
constexpr float WAH_SLOW_SECONDS = 0.06f;
constexpr float WAH_PICK_RATIO = 1.8f;
constexpr float WAH_PICK_FLOOR = 0.005f;
constexpr float WAH_REFRACTORY_SECONDS = 0.08f;
constexpr int WAH_CONTROL_FRAMES = 16;
constexpr float MATCH_DRIVES[] = {0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f};
constexpr float MATCH_FADE_START_SECONDS = 0.02f;
constexpr float MATCH_FADE_WINDOW_SECONDS = 0.01f;
constexpr int MATCH_FADE_MIN_WINDOWS = 6;
constexpr int MATCH_FADE_MIN_BANDS = 4;
constexpr float MATCH_FALLBACK_DRIVE = 1.0f;
constexpr float MATCH_LEVEL_SECONDS = 0.3f;
constexpr float MATCH_ANALYSIS_SECONDS = 0.2f;
constexpr float MATCH_MIN_NOTE_SECONDS = 0.1f;
constexpr float MATCH_MAX_NOTE_SECONDS = 0.35f;
constexpr int MATCH_MIN_NOTES = 3;
constexpr int MATCH_TONE_PASSES = 24;
constexpr double MATCH_BAND_FLOOR = 1e-5;
constexpr double MATCH_TRUSTED_SHARE = 0.004;
constexpr float MATCH_MAX_BOOST_DB = 6.0f;
constexpr float MATCH_MAX_STEP_DB = 6.0f;
constexpr float GUARD_MAX_RISE_DB = 3.0f;
constexpr int GUARD_PASSES = 8;
constexpr int GUARD_LOWEST_NOTE = 40;
constexpr int GUARD_HIGHEST_NOTE = 88;
constexpr int GUARD_NOTE_STEP = 4;
constexpr double GUARD_PLUCK_LEVEL = 0.1;
constexpr double GUARD_PLUCK_DECAY = 3.0;
constexpr int GUARD_HARMONICS = 12;
constexpr double GUARD_LOWEST_RATIO = 0.7;
constexpr double GUARD_HIGHEST_RATIO = 4.0;
constexpr std::size_t MATCH_RENDER_BLOCK = 512;
constexpr int WAH_TRACK_BANDS = 11;
constexpr double WAH_TRACK_BANDS_PER_OCTAVE = 3.0;
constexpr double WAH_TRACK_Q = 4.3;
constexpr float WAH_TRACK_FRAME_SECONDS = 0.01f;
constexpr float WAH_EARLY_START_SECONDS = 0.02f;
constexpr float WAH_EARLY_END_SECONDS = 0.08f;
constexpr float WAH_LATE_START_SECONDS = 0.2f;
constexpr double WAH_MIN_SWEEP_OCTAVES = 0.4;
constexpr int WAH_FIT_PASSES = 6;
constexpr float WAH_MIN_NOTE_SECONDS = 0.25f;

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
    : LeadAmp(channels, sampleRate, std::array<Cabinet, 1>{{{cabinet, cabinetFrames, cabinetRate}}}.data(),
          cabinet ? 1 : 0) {}

LeadAmp::LeadAmp(int channels, float sampleRate, const Cabinet* cabinets, std::size_t cabinetCount)
    : channels_(std::clamp(channels, 1, MAX_CHANNELS)),
      sampleRate_(sampleRate),
      couplingCoefficient_(static_cast<float>(std::exp(-2.0 * PI * COUPLING_HZ / sampleRate))),
      interstageCoefficient_(onePole(std::min(INTERSTAGE_LOW_PASS_HZ, 0.45f * sampleRate), sampleRate)),
      echoToneCoefficient_(onePole(ECHO_TONE_HZ, sampleRate)),
      echoLevel_(DEFAULT_ECHO_LEVEL),
      gainCoefficient_(static_cast<float>(1.0 - std::exp(-1.0 / (GAIN_SMOOTHING_MS * 0.001 * sampleRate)))),
      firstOffset_(std::tanh(FIRST_BIAS)),
      secondOffset_(std::tanh(SECOND_BIAS)),
      noiseReleaseCoefficient_(static_cast<float>(std::exp(-1.0 / (NOISE_RELEASE_SECONDS * sampleRate)))),
      wahFastRelease_(static_cast<float>(std::exp(-1.0 / (WAH_FAST_RELEASE_SECONDS * sampleRate)))),
      wahSlowCoefficient_(static_cast<float>(1.0 - std::exp(-1.0 / (WAH_SLOW_SECONDS * sampleRate)))),
      wahSweepCoefficient_(static_cast<float>(std::exp(-1.0 / (WAH_SWEEP_SECONDS * sampleRate)))),
      wahRefractoryFrames_(static_cast<std::size_t>(WAH_REFRACTORY_SECONDS * sampleRate)) {
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

    echoLength_ = static_cast<int>(std::ceil(MAX_ECHO_SECONDS * ECHO_SPREAD * sampleRate)) + 1;
    for (int channel = 0; channel < channels_; ++channel) {
        auto& state = states_[channel];
        state.first = Shaper{FIRST_BIAS, logCosh(FIRST_BIAS)};
        state.second = Shaper{SECOND_BIAS, logCosh(SECOND_BIAS)};
        state.echo.assign(static_cast<std::size_t>(echoLength_), 0.0f);
        state.echoFrames = std::clamp(static_cast<int>(std::lround(
            DEFAULT_ECHO_SECONDS * (channel % 2 ? ECHO_SPREAD : 1.0f) * sampleRate)), 1, echoLength_ - 1);
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

    wahSincePick_ = wahRefractoryFrames_;
    for (std::size_t index = 0; index < std::min<std::size_t>(cabinetCount, MAX_CABINETS); ++index) {
        cabinets_.push_back(loadCabinet(cabinets[index], sampleRate));
        cabinetLength_ = std::max(cabinetLength_, cabinets_.back().size());
    }
    for (auto& cabinet : cabinets_) {
        if (!cabinet.empty()) cabinet.resize(cabinetLength_, 0.0f);
    }
    for (int channel = 0; channel < channels_; ++channel) {
        states_[channel].cabinetHistory.assign(cabinetLength_ * 2, 0.0f);
    }
}

std::size_t LeadAmp::cabinetCount() const noexcept {
    return cabinets_.size();
}

std::vector<float> LeadAmp::loadCabinet(const Cabinet& source, float sampleRate) {
    const float* cabinet = source.samples;
    const std::size_t frames = source.frames;
    const float cabinetRate = source.rate;
    if (!cabinet || frames == 0 || !std::isfinite(cabinetRate) || cabinetRate <= 0.0f) return {};

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
    if (!std::isfinite(energy) || energy <= 0.0) return {};

    const auto scale = static_cast<float>(1.0 / std::sqrt(energy));
    for (float& sample : resampled) {
        sample *= scale;
    }
    return resampled;
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
    return TONE_LOWEST_HZ * std::exp2(static_cast<float>(band) / TONE_BANDS_PER_OCTAVE);
}

LeadAmp::Biquad LeadAmp::toneBand(float sampleRate, int band, float gainDb) noexcept {
    return peak(sampleRate, toneHz(band), gainDb, TONE_Q);
}

yarg_lead_amp_tone LeadAmp::defaultTone() noexcept {
    yarg_lead_amp_tone tone{};
    tone.size = sizeof(yarg_lead_amp_tone);
    tone.drive = 1.0f;
    tone.echo_seconds = DEFAULT_ECHO_SECONDS;
    tone.echo_level = DEFAULT_ECHO_LEVEL;
    tone.level_gain = 1.0f;
    tone.stem_gain = 0.0f;
    return tone;
}

bool LeadAmp::validTone(const yarg_lead_amp_tone& tone) noexcept {
    if (tone.size < sizeof(yarg_lead_amp_tone) || tone.cabinet >= MAX_CABINETS) return false;
    if (!std::isfinite(tone.drive) || tone.drive < MIN_DRIVE || tone.drive > MAX_DRIVE) return false;
    const auto wahInRange = [](float hz) { return std::isfinite(hz) && hz >= WAH_MIN_HZ && hz <= WAH_MAX_HZ; };
    if (!(tone.wah_start_hz == 0.0f && tone.wah_end_hz == 0.0f)
        && !(wahInRange(tone.wah_start_hz) && wahInRange(tone.wah_end_hz))) {
        return false;
    }
    if (!std::isfinite(tone.echo_seconds) || tone.echo_seconds < MIN_ECHO_SECONDS
        || tone.echo_seconds > MAX_ECHO_SECONDS) {
        return false;
    }
    if (!std::isfinite(tone.echo_level) || tone.echo_level < 0.0f || tone.echo_level > 1.0f) return false;
    for (float band : tone.bands_db) {
        if (!std::isfinite(band) || std::abs(band) > MAX_TONE_DB) return false;
    }
    return true;
}

void LeadAmp::setTone(const yarg_lead_amp_tone& tone) noexcept {
    targetDrive_ = tone.drive;
    cabinet_ = tone.cabinet < cabinets_.size() ? tone.cabinet : 0;
    wahOn_ = tone.wah_start_hz > 0.0f && tone.wah_end_hz > 0.0f;
    if (wahOn_) {
        wahStartOctaves_ = std::log2(tone.wah_start_hz);
        wahEndOctaves_ = std::log2(tone.wah_end_hz);
        wahControlFrame_ = 0;
    }
    echoLevel_ = tone.echo_level;
    for (int channel = 0; channel < channels_; ++channel) {
        states_[channel].echoFrames = std::clamp(static_cast<int>(std::lround(
            tone.echo_seconds * (channel % 2 ? ECHO_SPREAD : 1.0f) * sampleRate_)), 1, echoLength_ - 1);
    }
    for (int band = 0; band < TONE_BANDS; ++band) {
        filters_[TONE_FIRST + band] = toneBand(sampleRate_, band, tone.bands_db[band]);
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
    const std::size_t length = cabinetLength_;
    state.cabinetHistory[cabinetIndex_] = input;
    state.cabinetHistory[cabinetIndex_ + length] = input;

    const float* recent = state.cabinetHistory.data() + cabinetIndex_;
    const float* taps = cabinets_[cabinet_].data();
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

void LeadAmp::sweepWah(const float* frame) noexcept {
    float loudest = 0.0f;
    for (int channel = 0; channel < channels_; ++channel) {
        loudest = std::max(loudest, std::abs(frame[channel]));
    }
    wahFast_ = std::max(loudest, wahFast_ * wahFastRelease_);
    wahSlow_ += (wahFast_ - wahSlow_) * wahSlowCoefficient_;
    if (wahSincePick_ >= wahRefractoryFrames_ && wahFast_ > WAH_PICK_FLOOR && wahFast_ > WAH_PICK_RATIO * wahSlow_) {
        wahSweep_ = 1.0f;
        wahSincePick_ = 0;
    } else {
        wahSweep_ *= wahSweepCoefficient_;
        wahSincePick_ = std::min(wahSincePick_ + 1, wahRefractoryFrames_);
    }

    if (wahControlFrame_-- > 0) return;
    wahControlFrame_ = WAH_CONTROL_FRAMES - 1;
    const float octaves = wahEndOctaves_ + (wahStartOctaves_ - wahEndOctaves_) * wahSweep_;
    const float hz = std::min(std::exp2(octaves), 0.45f * sampleRate_);
    const auto g = static_cast<float>(std::tan(PI * hz / sampleRate_));
    wahA1_ = 1.0f / (1.0f + g * (g + 1.0f / WAH_Q));
    wahA2_ = g * wahA1_;
    wahA3_ = g * wahA2_;
}

float LeadAmp::wah(ChannelState& state, float input) const noexcept {
    const float high = input - state.wahLow;
    const float band = wahA1_ * state.wahBand + wahA2_ * high;
    const float low = state.wahLow + wahA2_ * state.wahBand + wahA3_ * high;
    state.wahBand = 2.0f * band - state.wahBand;
    state.wahLow = 2.0f * low - state.wahLow;
    return WAH_DRY * input + WAH_PEAK_GAIN / WAH_Q * band;
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
        if (wahOn_) sweepWah(samples + frame * channels_);
        for (int channel = 0; channel < channels_; ++channel) {
            float& sample = samples[frame * channels_ + channel];
            auto& state = states_[channel];

            const float picked = sample + pickupNoise;
            float signal = run(PRE_HIGH_PASS, state, wahOn_ ? wah(state, picked) : picked);
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
            if (cabinets_.empty() || cabinets_[cabinet_].empty()) {
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

            sample = (amp + echoLevel_ * echo) * gain_;
        }

        echoIndex_ = (echoIndex_ + 1) % echoLength_;
        if (cabinetLength_ > 0) {
            cabinetIndex_ = (cabinetIndex_ == 0 ? static_cast<int>(cabinetLength_) : cabinetIndex_) - 1;
        }
    }
}

void YARG_BASS_CALLBACK leadAmpDspProc(std::uint32_t, std::uint32_t,
    void* buffer, std::uint32_t length, void* user) noexcept {
    if (!buffer || !user || length == 0 || length % sizeof(float) != 0) return;

    auto* state = static_cast<yarg_lead_amp_dsp*>(user);
    const std::uint32_t toneVersion = state->toneVersion.load(std::memory_order_acquire);
    if (toneVersion != state->appliedToneVersion) {
        std::array<std::uint32_t, yarg_lead_amp_dsp::TONE_WORDS> words{};
        for (std::size_t word = 0; word < words.size(); ++word) {
            words[word] = state->toneWords[word].load(std::memory_order_relaxed);
        }
        if (state->toneVersion.load(std::memory_order_acquire) == toneVersion) {
            yarg_lead_amp_tone tone;
            std::memcpy(&tone, words.data(), sizeof(tone));
            state->amp.setTone(tone);
            state->appliedToneVersion = toneVersion;
        }
    }
    const float gain = bitCast<float>(state->gainBits.load(std::memory_order_relaxed));
    const std::size_t frames = length / sizeof(float) / static_cast<std::size_t>(state->channelCount);
    state->amp.process(static_cast<float*>(buffer), frames, gain);
}

bool cabinetList(const float* samples, const std::uint32_t* frames, const float* rates, std::size_t count,
    std::vector<Cabinet>& cabinets) {
    cabinets.clear();
    if (count == 0) return true;
    if (!samples || !frames || !rates || count > LeadAmp::MAX_CABINETS) return false;
    std::size_t offset = 0;
    for (std::size_t index = 0; index < count; ++index) {
        if (!std::isfinite(rates[index]) || rates[index] <= 0.0f) return false;
        cabinets.push_back({samples + offset, frames[index], rates[index]});
        offset += frames[index];
    }
    return true;
}

int leadAmpDspAttach(const BassCoreBindings& bass, std::uint32_t channel, float gain,
    const Cabinet* cabinets, std::size_t cabinetCount,
    int priority, yarg_lead_amp_dsp** dsp, int* bassError) noexcept {
    if (dsp) *dsp = nullptr;
    if (bassError) *bassError = 0;
    if (!dsp || channel == 0 || !std::isfinite(gain) || gain < 0.0f || gain > LeadAmp::MAX_GAIN) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    if (cabinetCount > 0 && !cabinets) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
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
            static_cast<float>(info.frequency), gain, cabinets, cabinetCount);
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

int leadAmpDspSetTone(yarg_lead_amp_dsp* dsp, const yarg_lead_amp_tone* tone) noexcept {
    if (!dsp || !tone || !LeadAmp::validTone(*tone)) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    std::array<std::uint32_t, yarg_lead_amp_dsp::TONE_WORDS> words{};
    std::memcpy(words.data(), tone, sizeof(yarg_lead_amp_tone));
    for (std::size_t word = 0; word < words.size(); ++word) {
        dsp->toneWords[word].store(words[word], std::memory_order_relaxed);
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
    double level = 0.0;
};

using BandMask = std::array<bool, LeadAmp::TONE_BANDS>;

double median(std::vector<double> values) {
    if (values.empty()) return 0.0;
    const auto middle = values.begin() + static_cast<std::ptrdiff_t>(values.size() / 2);
    std::nth_element(values.begin(), middle, values.end());
    return *middle;
}

double bandEnergy(const float* samples, std::size_t frames, float sampleRate, float hz) {
    const double w0 = 2.0 * PI * std::min(hz, 0.45f * sampleRate) / sampleRate;
    const double alpha = std::sin(w0) / (2.0 * TONE_Q);
    const double a0 = 1.0 + alpha;
    const double b0 = alpha / a0;
    const double a1 = -2.0 * std::cos(w0) / a0;
    const double a2 = (1.0 - alpha) / a0;
    std::array<double, 2> z1{}, z2{};
    double energy = 0.0;
    for (std::size_t frame = 0; frame < frames; ++frame) {
        double signal = samples[frame];
        for (std::size_t stage = 0; stage < z1.size(); ++stage) {
            const double output = b0 * signal + z1[stage];
            z1[stage] = -a1 * output + z2[stage];
            z2[stage] = -b0 * signal - a2 * output;
            signal = output;
        }
        energy += signal * signal;
    }
    return energy;
}

double slope(const std::vector<double>& x, const std::vector<double>& y) {
    double meanX = 0.0, meanY = 0.0;
    for (std::size_t index = 0; index < x.size(); ++index) {
        meanX += x[index];
        meanY += y[index];
    }
    meanX /= static_cast<double>(x.size());
    meanY /= static_cast<double>(y.size());
    double covariance = 0.0, variance = 0.0;
    for (std::size_t index = 0; index < x.size(); ++index) {
        covariance += (x[index] - meanX) * (y[index] - meanY);
        variance += (x[index] - meanX) * (x[index] - meanX);
    }
    return variance > 0.0 ? covariance / variance : 0.0;
}

struct BandDecay {
    std::array<double, LeadAmp::TONE_BANDS> slopes{};
    std::array<double, LeadAmp::TONE_BANDS> shares{};
    bool valid = false;
};

BandDecay bandDecay(const std::vector<float>& note, float sampleRate) {
    const auto window = std::max<std::size_t>(1, static_cast<std::size_t>(MATCH_FADE_WINDOW_SECONDS * sampleRate));
    const auto start = static_cast<std::size_t>(MATCH_FADE_START_SECONDS * sampleRate);
    const auto length = std::min<std::size_t>(note.size(),
        static_cast<std::size_t>(MATCH_ANALYSIS_SECONDS * sampleRate));
    BandDecay decay;
    if (length < start + window * MATCH_FADE_MIN_WINDOWS) return decay;

    std::vector<double> times;
    for (std::size_t first = start; first + window <= length; first += window) {
        times.push_back(static_cast<double>(first) / sampleRate);
    }
    double total = 0.0;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        const double w0 = 2.0 * PI * std::min(LeadAmp::toneHz(band), 0.45f * sampleRate) / sampleRate;
        const double alpha = std::sin(w0) / (2.0 * TONE_Q);
        const double a0 = 1.0 + alpha;
        const double b0 = alpha / a0;
        const double a1 = -2.0 * std::cos(w0) / a0;
        const double a2 = (1.0 - alpha) / a0;
        std::array<double, 2> z1{}, z2{};
        std::vector<double> levels;
        double energy = 0.0, windowEnergy = 0.0;
        for (std::size_t frame = 0; frame < start + times.size() * window; ++frame) {
            double signal = note[frame];
            for (std::size_t stage = 0; stage < z1.size(); ++stage) {
                const double output = b0 * signal + z1[stage];
                z1[stage] = -a1 * output + z2[stage];
                z2[stage] = -b0 * signal - a2 * output;
                signal = output;
            }
            energy += signal * signal;
            if (frame < start) continue;
            windowEnergy += signal * signal;
            if ((frame - start + 1) % window == 0) {
                levels.push_back(10.0 * std::log10(windowEnergy + MATCH_BAND_FLOOR * MATCH_BAND_FLOOR));
                windowEnergy = 0.0;
            }
        }
        decay.slopes[band] = slope(times, levels);
        decay.shares[band] = energy;
        total += energy;
    }
    if (!(total > 0.0)) return decay;
    for (double& share : decay.shares) {
        share /= total;
    }
    decay.valid = true;
    return decay;
}

std::vector<BandMask> fadeMasks(const std::vector<std::vector<float>>& notes, float sampleRate) {
    std::vector<BandMask> masks;
    for (const auto& note : notes) {
        const auto decay = bandDecay(note, sampleRate);
        BandMask mask{};
        for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
            mask[band] = decay.valid && decay.shares[band] > MATCH_TRUSTED_SHARE;
        }
        masks.push_back(mask);
    }
    return masks;
}

std::optional<double> typicalFade(const std::vector<std::vector<float>>& notes, const std::vector<BandMask>& masks,
    float sampleRate) {
    std::vector<double> fades;
    for (std::size_t index = 0; index < notes.size(); ++index) {
        const auto decay = bandDecay(notes[index], sampleRate);
        if (!decay.valid) continue;
        std::vector<double> octaves, slopes;
        for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
            if (!masks[index][band]) continue;
            octaves.push_back(band / 3.0);
            slopes.push_back(decay.slopes[band]);
        }
        if (octaves.size() >= MATCH_FADE_MIN_BANDS) fades.push_back(slope(octaves, slopes));
    }
    if (fades.size() < MATCH_MIN_NOTES) return std::nullopt;
    std::sort(fades.begin(), fades.end());
    const std::size_t trim = fades.size() / 4;
    double sum = 0.0;
    for (std::size_t index = trim; index < fades.size() - trim; ++index) {
        sum += fades[index];
    }
    return sum / static_cast<double>(fades.size() - 2 * trim);
}

ToneMeasure measure(const std::vector<std::vector<float>>& notes, float sampleRate) {
    const auto frames = [sampleRate](float seconds) {
        return static_cast<std::size_t>(seconds * sampleRate);
    };
    ToneMeasure tone;
    std::vector<double> levels;
    int spectra = 0;
    for (const auto& note : notes) {
        const float* samples = note.data();
        const std::size_t length = note.size();
        levels.push_back(kWeightedLevel(samples, std::min(length, frames(MATCH_LEVEL_SECONDS)), sampleRate));

        std::array<double, LeadAmp::TONE_BANDS> bands{};
        double total = 0.0;
        const auto spectrum = std::min(length, frames(MATCH_ANALYSIS_SECONDS));
        for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
            bands[band] = bandEnergy(samples, spectrum, sampleRate, LeadAmp::toneHz(band));
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
    tone.level = median(std::move(levels));
    return tone;
}

std::vector<std::vector<float>> render(const std::vector<MatchNote>& notes, float sampleRate, const Cabinet* cabinet,
    float drive, float wahStartHz = 0.0f, float wahEndHz = 0.0f) {
    auto tone = LeadAmp::defaultTone();
    tone.drive = drive;
    tone.wah_start_hz = wahStartHz;
    tone.wah_end_hz = wahEndHz;
    tone.echo_level = 0.0f;
    std::vector<std::vector<float>> rendered;
    rendered.reserve(notes.size());
    for (const auto& note : notes) {
        LeadAmp amp(1, sampleRate, cabinet, cabinet ? 1 : 0);
        amp.setTone(tone);
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

std::optional<float> fitDrive(double target, const std::array<double, std::size(MATCH_DRIVES)>& fades) {
    for (std::size_t index = 0; index + 1 < fades.size(); ++index) {
        const double low = fades[index];
        const double high = fades[index + 1];
        if (low == high || (target - low) * (target - high) > 0.0) continue;
        const double position = (target - low) / (high - low);
        const double logDrive = std::log(MATCH_DRIVES[index])
            + position * (std::log(MATCH_DRIVES[index + 1]) - std::log(MATCH_DRIVES[index]));
        return static_cast<float>(std::exp(logDrive));
    }
    const std::size_t last = fades.size() - 1;
    for (const auto [edge, inner] : {std::pair{std::size_t{0}, std::size_t{1}}, std::pair{last, last - 1}}) {
        const double step = fades[edge] - fades[inner];
        const double beyond = target - fades[edge];
        if (beyond * step >= 0.0 && std::abs(beyond) <= std::abs(step)) return MATCH_DRIVES[edge];
    }
    return std::nullopt;
}

using TrustedBands = std::array<bool, LeadAmp::TONE_BANDS>;

TrustedBands trustedBands(const ToneMeasure& amp) {
    TrustedBands trusted{};
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        trusted[band] = amp.bands[band] > MATCH_TRUSTED_SHARE;
    }
    return trusted;
}

void correct(std::array<float, LeadAmp::TONE_BANDS>& bandsDb, const TrustedBands& trusted, const ToneMeasure& stem,
    const ToneMeasure& amp) {
    double stemTotal = 0.0, ampTotal = 0.0;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        if (!trusted[band]) continue;
        stemTotal += stem.bands[band];
        ampTotal += amp.bands[band];
    }
    if (!(stemTotal > 0.0) || !(ampTotal > 0.0)) return;

    double weighted = 0.0, weight = 0.0;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        if (!trusted[band]) continue;
        const double stemShare = stem.bands[band] / stemTotal;
        const double ampShare = amp.bands[band] / ampTotal;
        if (stemShare > MATCH_BAND_FLOOR && ampShare > MATCH_BAND_FLOOR) {
            bandsDb[band] += static_cast<float>(10.0 * std::log10(stemShare / ampShare));
        }
        weighted += stemShare * bandsDb[band];
        weight += stemShare;
    }
    const auto centre = static_cast<float>(weight > 0.0 ? weighted / weight : 0.0);
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        if (trusted[band]) {
            bandsDb[band] = std::clamp(bandsDb[band] - centre, -LeadAmp::MAX_TONE_DB, MATCH_MAX_BOOST_DB);
        }
    }

    int first = -1, last = -1;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        if (!trusted[band]) continue;
        if (first < 0) first = band;
        last = band;
    }
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        if (trusted[band]) continue;
        if (first < 0) {
            bandsDb[band] = 0.0f;
        } else if (band < first) {
            bandsDb[band] = band == first - 1 ? 0.5f * bandsDb[first] : 0.0f;
        } else if (band > last) {
            bandsDb[band] = band == last + 1 ? 0.5f * bandsDb[last] : 0.0f;
        } else {
            int below = band - 1, above = band + 1;
            while (!trusted[below]) --below;
            while (!trusted[above]) ++above;
            const float position = static_cast<float>(band - below) / static_cast<float>(above - below);
            bandsDb[band] = bandsDb[below] + position * (bandsDb[above] - bandsDb[below]);
        }
    }

    for (int band = 1; band < LeadAmp::TONE_BANDS; ++band) {
        bandsDb[band] = std::clamp(bandsDb[band], bandsDb[band - 1] - MATCH_MAX_STEP_DB,
            bandsDb[band - 1] + MATCH_MAX_STEP_DB);
    }
    for (int band = LeadAmp::TONE_BANDS - 2; band >= 0; --band) {
        bandsDb[band] = std::clamp(bandsDb[band], bandsDb[band + 1] - MATCH_MAX_STEP_DB,
            bandsDb[band + 1] + MATCH_MAX_STEP_DB);
    }
}

std::vector<std::vector<float>> rangeNotes(float sampleRate) {
    const auto frames = static_cast<std::size_t>(MATCH_MAX_NOTE_SECONDS * sampleRate);
    std::vector<std::vector<float>> notes;
    for (int note = GUARD_LOWEST_NOTE; note <= GUARD_HIGHEST_NOTE; note += GUARD_NOTE_STEP) {
        const double hz = 440.0 * std::exp2((note - 69) / 12.0);
        std::vector<float> samples(frames);
        for (std::size_t frame = 0; frame < frames; ++frame) {
            const double seconds = static_cast<double>(frame) / sampleRate;
            double value = 0.0;
            for (int harmonic = 1; harmonic <= GUARD_HARMONICS && hz * harmonic < 0.45 * sampleRate; ++harmonic) {
                value += std::sin(2.0 * PI * hz * harmonic * seconds) / harmonic;
            }
            samples[frame] = static_cast<float>(GUARD_PLUCK_LEVEL * std::exp(-GUARD_PLUCK_DECAY * seconds) * value);
        }
        notes.push_back(std::move(samples));
    }
    return notes;
}

std::vector<double> noteLevels(const std::vector<std::vector<float>>& notes, float sampleRate) {
    const auto frames = static_cast<std::size_t>(MATCH_LEVEL_SECONDS * sampleRate);
    std::vector<double> levels;
    for (const auto& note : notes) {
        levels.push_back(kWeightedLevel(note.data(), std::min(frames, note.size()), sampleRate));
    }
    return levels;
}

}

double toneDistance(const ToneMeasure& stem, const ToneMeasure& amp) {
    const auto trusted = trustedBands(amp);
    double stemTotal = 0.0, ampTotal = 0.0;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        if (!trusted[band]) continue;
        stemTotal += stem.bands[band];
        ampTotal += amp.bands[band];
    }
    if (!(stemTotal > 0.0) || !(ampTotal > 0.0)) return HUGE_VAL;

    std::array<double, LeadAmp::TONE_BANDS> differences{};
    double weighted = 0.0, weight = 0.0;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        const double stemShare = stem.bands[band] / stemTotal;
        if (!trusted[band] || !(stemShare > MATCH_BAND_FLOOR)) continue;
        differences[band] = 10.0 * std::log10(stemShare / (amp.bands[band] / ampTotal));
        weighted += stemShare * differences[band];
        weight += stemShare;
    }
    if (!(weight > 0.0)) return HUGE_VAL;
    const double centre = weighted / weight;
    double spread = 0.0;
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        const double stemShare = stem.bands[band] / stemTotal;
        if (!trusted[band] || !(stemShare > MATCH_BAND_FLOOR)) continue;
        spread += stemShare * (differences[band] - centre) * (differences[band] - centre);
    }
    return std::sqrt(spread / weight);
}

struct Sweep {
    double early = 0.0;
    double late = 0.0;
};

Sweep sweep(const std::vector<float>& note, float sampleRate) {
    const auto frameLength = std::max<std::size_t>(1, static_cast<std::size_t>(WAH_TRACK_FRAME_SECONDS * sampleRate));
    const std::size_t frameCount = note.size() / frameLength;
    std::vector<double> weighted(frameCount, 0.0), total(frameCount, 0.0);
    for (int band = 0; band < WAH_TRACK_BANDS; ++band) {
        const double hz = LeadAmp::WAH_MIN_HZ * std::exp2(band / WAH_TRACK_BANDS_PER_OCTAVE);
        const double w0 = 2.0 * PI * std::min(hz, 0.45 * sampleRate) / sampleRate;
        const double alpha = std::sin(w0) / (2.0 * WAH_TRACK_Q);
        const double b0 = alpha / (1.0 + alpha);
        const double a1 = -2.0 * std::cos(w0) / (1.0 + alpha);
        const double a2 = (1.0 - alpha) / (1.0 + alpha);
        double z1 = 0.0, z2 = 0.0;
        for (std::size_t frame = 0; frame < frameCount * frameLength; ++frame) {
            const double input = note[frame];
            const double output = b0 * input + z1;
            z1 = -a1 * output + z2;
            z2 = -b0 * input - a2 * output;
            weighted[frame / frameLength] += output * output * std::log2(hz);
            total[frame / frameLength] += output * output;
        }
    }

    const auto average = [&](float startSeconds, float endSeconds) {
        double sum = 0.0;
        int count = 0;
        for (std::size_t frame = 0; frame < frameCount; ++frame) {
            const double seconds = static_cast<double>(frame * frameLength) / sampleRate;
            if (seconds < startSeconds || seconds >= endSeconds || !(total[frame] > 0.0)) continue;
            sum += weighted[frame] / total[frame];
            ++count;
        }
        return count > 0 ? sum / count : std::log2(static_cast<double>(LeadAmp::WAH_MIN_HZ));
    };
    return {average(WAH_EARLY_START_SECONDS, WAH_EARLY_END_SECONDS), average(WAH_LATE_START_SECONDS, MATCH_MAX_NOTE_SECONDS)};
}

std::vector<Sweep> sweeps(const std::vector<std::vector<float>>& notes, float sampleRate) {
    std::vector<Sweep> result;
    result.reserve(notes.size());
    for (const auto& note : notes) {
        result.push_back(sweep(note, sampleRate));
    }
    return result;
}

Sweep typical(const std::vector<Sweep>& noteSweeps) {
    std::vector<double> early, late;
    for (const auto& noteSweep : noteSweeps) {
        early.push_back(noteSweep.early);
        late.push_back(noteSweep.late);
    }
    return {median(std::move(early)), median(std::move(late))};
}

bool hasWah(const std::vector<Sweep>& stem, const std::vector<Sweep>& amp) {
    std::vector<double> excess;
    for (std::size_t note = 0; note < stem.size(); ++note) {
        excess.push_back(std::abs((stem[note].late - stem[note].early) - (amp[note].late - amp[note].early)));
    }
    return median(std::move(excess)) > WAH_MIN_SWEEP_OCTAVES;
}

float wahHz(double octaves) {
    return std::clamp(static_cast<float>(std::exp2(octaves)), LeadAmp::WAH_MIN_HZ, LeadAmp::WAH_MAX_HZ);
}

int leadAmpMatch(float sampleRate, const Cabinet* cabinets, std::size_t cabinetCount,
    const float* stem, const float* di, const std::uint32_t* noteFrames, std::size_t noteCount,
    yarg_lead_amp_tone* tone) noexcept {
    if (!tone || tone->size < sizeof(yarg_lead_amp_tone)) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    *tone = LeadAmp::defaultTone();
    if (!stem || !di || !noteFrames || !std::isfinite(sampleRate) || sampleRate <= 0.0f) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }
    if ((cabinetCount > 0 && !cabinets) || cabinetCount > LeadAmp::MAX_CABINETS) {
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
        const Cabinet* defaultCabinet = cabinetCount > 0 ? &cabinets[0] : nullptr;
        const double defaultLevel = measure(render(notes, sampleRate, defaultCabinet, 1.0f), sampleRate).level;

        const auto masks = fadeMasks(stemNotes, sampleRate);
        const auto targetFade = typicalFade(stemNotes, masks, sampleRate);
        const auto fitDriveOn = [&](const Cabinet* cabinet, float wahStart, float wahEnd) {
            if (!targetFade) return MATCH_FALLBACK_DRIVE;
            std::array<double, std::size(MATCH_DRIVES)> fades{};
            for (std::size_t index = 0; index < fades.size(); ++index) {
                const auto heard = typicalFade(render(notes, sampleRate, cabinet, MATCH_DRIVES[index], wahStart,
                    wahEnd), masks, sampleRate);
                if (!heard) return MATCH_FALLBACK_DRIVE;
                fades[index] = *heard;
            }
            return std::clamp(fitDrive(*targetFade, fades).value_or(MATCH_FALLBACK_DRIVE), LeadAmp::MIN_DRIVE,
                LeadAmp::MAX_DRIVE);
        };
        const auto closestCabinet = [&](float drive) {
            std::size_t chosen = 0;
            double closest = HUGE_VAL;
            for (std::size_t index = 0; index < cabinetCount; ++index) {
                const double distance = toneDistance(target, measure(render(notes, sampleRate, &cabinets[index], drive),
                    sampleRate));
                if (distance < closest) {
                    closest = distance;
                    chosen = index;
                }
            }
            return chosen;
        };

        float fitted = fitDriveOn(defaultCabinet, 0.0f, 0.0f);
        std::size_t chosen = closestCabinet(fitted);
        if (chosen != 0) {
            fitted = fitDriveOn(&cabinets[chosen], 0.0f, 0.0f);
            const std::size_t rechosen = closestCabinet(fitted);
            if (rechosen != chosen) {
                chosen = rechosen;
                fitted = fitDriveOn(&cabinets[chosen], 0.0f, 0.0f);
            }
        }
        const Cabinet* cabinet = cabinetCount > 0 ? &cabinets[chosen] : nullptr;

        std::vector<MatchNote> wahNotes;
        std::vector<std::vector<float>> wahStemNotes;
        const auto wahFrames = static_cast<std::size_t>(WAH_MIN_NOTE_SECONDS * sampleRate);
        for (std::size_t index = 0; index < notes.size(); ++index) {
            if (notes[index].frames < wahFrames) continue;
            wahNotes.push_back(notes[index]);
            wahStemNotes.push_back(stemNotes[index]);
        }

        float wahStart = 0.0f, wahEnd = 0.0f;
        const auto stemSweeps = sweeps(wahStemNotes, sampleRate);
        if (wahNotes.size() >= MATCH_MIN_NOTES
            && hasWah(stemSweeps, sweeps(render(wahNotes, sampleRate, cabinet, fitted), sampleRate))) {
            const auto wanted = typical(stemSweeps);
            wahStart = wahHz(wanted.early);
            wahEnd = wahHz(wanted.late);
            for (int pass = 0; pass < WAH_FIT_PASSES; ++pass) {
                const auto heard = typical(sweeps(render(wahNotes, sampleRate, cabinet, fitted, wahStart, wahEnd),
                    sampleRate));
                wahStart = wahHz(std::log2(wahStart) + wanted.early - heard.early);
                wahEnd = wahHz(std::log2(wahEnd) + wanted.late - heard.late);
            }
            fitted = fitDriveOn(cabinet, wahStart, wahEnd);
        }

        std::array<float, LeadAmp::TONE_BANDS> bands{};
        const auto amped = render(notes, sampleRate, cabinet, fitted, wahStart, wahEnd);
        auto rendered = measure(amped, sampleRate);
        const auto trusted = trustedBands(rendered);
        for (int pass = 0; pass < MATCH_TONE_PASSES; ++pass) {
            correct(bands, trusted, target, rendered);
            rendered = measure(equalize(amped, sampleRate, bands), sampleRate);
        }

        const auto range = rangeNotes(sampleRate);
        std::vector<MatchNote> rangeMatch;
        for (const auto& note : range) {
            rangeMatch.push_back({note.data(), note.data(), note.size()});
        }
        const auto defaultLevels = noteLevels(render(rangeMatch, sampleRate, defaultCabinet, 1.0f), sampleRate);
        const auto rangeAmped = render(rangeMatch, sampleRate, cabinet, fitted, wahStart, wahEnd);
        const auto loudest = [&](double levelGain) {
            const auto levels = noteLevels(equalize(rangeAmped, sampleRate, bands), sampleRate);
            std::size_t worst = 0;
            double worstRise = -HUGE_VAL;
            for (std::size_t note = 0; note < levels.size(); ++note) {
                if (!(levels[note] > 0.0) || !(defaultLevels[note] > 0.0)) continue;
                const double rise = 20.0 * std::log10(levels[note] * levelGain / defaultLevels[note]);
                if (rise > worstRise) {
                    worstRise = rise;
                    worst = note;
                }
            }
            return std::pair{worst, worstRise};
        };
        const bool leveled = rendered.level > 0.0 && defaultLevel > 0.0;
        for (int pass = 0; pass < GUARD_PASSES && leveled; ++pass) {
            const auto [worst, worstRise] = loudest(defaultLevel / rendered.level);
            if (worstRise <= GUARD_MAX_RISE_DB) break;

            const double hz = 440.0 * std::exp2((GUARD_LOWEST_NOTE + GUARD_NOTE_STEP * static_cast<int>(worst) - 69) / 12.0);
            bool lowered = false;
            for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
                const double bandHz = LeadAmp::toneHz(band);
                if (bandHz < hz * GUARD_LOWEST_RATIO || bandHz > hz * GUARD_HIGHEST_RATIO || bands[band] <= 0.0f) continue;
                bands[band] = std::max(0.0f, bands[band] - static_cast<float>(worstRise - GUARD_MAX_RISE_DB));
                lowered = true;
            }
            if (!lowered) break;
            rendered = measure(equalize(amped, sampleRate, bands), sampleRate);
        }

        double levelGain = leveled ? defaultLevel / rendered.level : 1.0;
        if (leveled) {
            const double excess = loudest(levelGain).second - GUARD_MAX_RISE_DB;
            if (excess > 0.0) levelGain *= std::pow(10.0, -excess / 20.0);
        }

        const double stemGain = target.level > 0.0 && defaultLevel > 0.0
            ? std::clamp(target.level / defaultLevel, 0.0, 1000.0)
            : 0.0;

        tone->cabinet = static_cast<std::uint32_t>(chosen);
        tone->drive = fitted;
        tone->wah_start_hz = wahStart;
        tone->wah_end_hz = wahEnd;
        std::copy(bands.begin(), bands.end(), tone->bands_db);
        tone->level_gain = static_cast<float>(levelGain);
        tone->stem_gain = static_cast<float>(stemGain);
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
    int channels, float sampleRate, float gain, const yarg::audio::Cabinet* cabinets, std::size_t cabinetCount)
    : bass(bindings), channel(channelHandle), channelCount(channels),
      gainBits(yarg::audio::bitCast<std::uint32_t>(gain)),
      amp(channels, sampleRate, cabinets, cabinetCount) {}
