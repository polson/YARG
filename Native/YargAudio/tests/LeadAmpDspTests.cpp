#include "dsp/LeadAmpDsp.h"
#include "Test.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <vector>

using yarg::audio::LeadAmp;
using Tone = std::array<float, LeadAmp::TONE_BANDS>;

namespace {

constexpr double PI = 3.14159265358979323846;
constexpr int SAMPLE_RATE = 48000;
constexpr int CHANNELS = 2;

std::vector<float> sine(double frequency, double amplitude, int frames) {
    std::vector<float> samples(static_cast<std::size_t>(frames) * CHANNELS);
    for (int frame = 0; frame < frames; ++frame) {
        const auto value = static_cast<float>(amplitude * std::sin(2 * PI * frequency * frame / SAMPLE_RATE));
        samples[frame * CHANNELS] = value;
        samples[frame * CHANNELS + 1] = value;
    }
    return samples;
}

double rms(const std::vector<float>& samples, int channel, int firstFrame, int frames) {
    double sum = 0;
    for (int frame = firstFrame; frame < firstFrame + frames; ++frame) {
        sum += samples[frame * CHANNELS + channel] * samples[frame * CHANNELS + channel];
    }
    return std::sqrt(sum / frames);
}

double tonePower(const std::vector<float>& samples, int firstFrame, int frames, double frequency) {
    double real = 0;
    double imaginary = 0;
    for (int frame = firstFrame; frame < firstFrame + frames; ++frame) {
        const double window = 0.5 - 0.5 * std::cos(2 * PI * (frame - firstFrame) / frames);
        real += samples[frame * CHANNELS] * window * std::cos(2 * PI * frequency * frame / SAMPLE_RATE);
        imaginary += samples[frame * CHANNELS] * window * std::sin(2 * PI * frequency * frame / SAMPLE_RATE);
    }
    return real * real + imaginary * imaginary;
}

std::vector<float> run(std::vector<float> samples, float gain, const std::vector<float>& cabinet = {},
    float drive = 1.0f, const Tone& tone = {}) {
    LeadAmp amp(CHANNELS, SAMPLE_RATE, cabinet.data(), cabinet.size(), SAMPLE_RATE);
    amp.setTone(drive, tone.data());
    const std::size_t frames = samples.size() / CHANNELS;
    const std::size_t block = 512;
    for (std::size_t first = 0; first < frames; first += block) {
        amp.process(samples.data() + first * CHANNELS, std::min(block, frames - first), gain);
    }
    return samples;
}

void silenceStaysSilent() {
    const std::vector<float> input(static_cast<std::size_t>(SAMPLE_RATE / 4) * CHANNELS, 0.0f);
    for (float sample : run(input, 1.0f)) {
        REQUIRE(std::abs(sample) < 1e-6f);
    }
}

void addsHarmonics() {
    const auto output = run(sine(330, 0.1, SAMPLE_RATE / 2), 1.0f);
    const int first = SAMPLE_RATE / 4;
    const int frames = 8192;
    REQUIRE(tonePower(output, first, frames, 990) > 0.01 * tonePower(output, first, frames, 330));
}

void compressesLoudAndQuietNotes() {
    const int first = SAMPLE_RATE / 4;
    const int frames = SAMPLE_RATE / 10;
    const double quiet = rms(run(sine(330, 0.03, SAMPLE_RATE / 2), 1.0f), 0, first, frames);
    const double loud = rms(run(sine(330, 0.3, SAMPLE_RATE / 2), 1.0f), 0, first, frames);
    REQUIRE(loud / quiet < 3.0);
}

void gainScalesOutput() {
    const auto input = sine(330, 0.1, SAMPLE_RATE / 2);
    const int first = SAMPLE_RATE / 4;
    const int frames = SAMPLE_RATE / 10;
    const double unity = rms(run(input, 1.0f), 0, first, frames);
    const double doubled = rms(run(input, 2.0f), 0, first, frames);
    REQUIRE(std::abs(doubled / unity - 2.0) < 0.01);
}

void echoesAfterTheNoteStops() {
    auto input = sine(330, 0.1, SAMPLE_RATE);
    std::fill(input.begin() + SAMPLE_RATE / 10 * CHANNELS, input.end(), 0.0f);
    const auto output = run(input, 1.0f);
    const int silentStart = SAMPLE_RATE / 4;
    const int echoStart = SAMPLE_RATE * 2 / 5;
    const int frames = SAMPLE_RATE / 20;
    REQUIRE(rms(output, 0, echoStart, frames) > 10 * rms(output, 0, silentStart, frames));
}

void channelsEchoAtDifferentTimes() {
    auto input = sine(330, 0.1, SAMPLE_RATE);
    std::fill(input.begin() + SAMPLE_RATE / 20 * CHANNELS, input.end(), 0.0f);
    const auto output = run(input, 1.0f);
    const int leftEcho = static_cast<int>(0.38 * SAMPLE_RATE);
    const int frames = SAMPLE_RATE / 50;
    REQUIRE(rms(output, 0, leftEcho, frames) > 2 * rms(output, 1, leftEcho, frames));
}

void staysFiniteWhenOverdriven() {
    for (float sample : run(sine(110, 50.0, SAMPLE_RATE / 4), 4.0f)) {
        REQUIRE(std::isfinite(sample));
        REQUIRE(std::abs(sample) < 20.0f);
    }
}

std::vector<float> impulse(int delay, float level) {
    std::vector<float> cabinet(static_cast<std::size_t>(delay) + 1, 0.0f);
    cabinet[delay] = level;
    return cabinet;
}

void cabinetDelaysLikeItsImpulse() {
    const auto input = sine(330, 0.1, SAMPLE_RATE / 2);
    for (int delay : {3, 1000}) {
        const auto direct = run(input, 1.0f, impulse(0, 1.0f));
        const auto delayed = run(input, 1.0f, impulse(delay, 1.0f));
        for (std::size_t sample = 0; sample < direct.size() - delay * CHANNELS; ++sample) {
            REQUIRE(std::abs(delayed[sample + delay * CHANNELS] - direct[sample]) < 1e-5f);
        }
    }
}

void cabinetLevelIsNormalized() {
    const auto input = sine(330, 0.1, SAMPLE_RATE / 2);
    const auto quiet = run(input, 1.0f, impulse(0, 0.25f));
    const auto loud = run(input, 1.0f, impulse(0, 4.0f));
    for (std::size_t sample = 0; sample < quiet.size(); ++sample) {
        REQUIRE(std::abs(quiet[sample] - loud[sample]) < 1e-5f);
    }
}

void silentCabinetFallsBackToFilters() {
    const auto input = sine(330, 0.1, SAMPLE_RATE / 2);
    const auto filtered = run(input, 1.0f);
    const auto fallback = run(input, 1.0f, std::vector<float>(256, 0.0f));
    REQUIRE(filtered == fallback);
}

void cabinetShapesTone() {
    std::vector<float> cabinet(512);
    for (std::size_t tap = 0; tap < cabinet.size(); ++tap) {
        cabinet[tap] = static_cast<float>(std::exp(-0.02 * tap) * std::cos(2 * PI * 2000 * tap / SAMPLE_RATE));
    }
    const auto input = sine(330, 0.1, SAMPLE_RATE / 2);
    const auto output = run(input, 1.0f, cabinet);
    const int first = SAMPLE_RATE / 4;
    const int frames = 8192;
    const double filtered = tonePower(run(input, 1.0f), first, frames, 1980) / tonePower(run(input, 1.0f), first, frames, 330);
    REQUIRE(tonePower(output, first, frames, 1980) / tonePower(output, first, frames, 330) > 4 * filtered);
    for (float sample : output) {
        REQUIRE(std::isfinite(sample));
    }
}

void longCabinetIsTruncated() {
    std::vector<float> cabinet(SAMPLE_RATE, 0.0f);
    cabinet[0] = 1.0f;
    cabinet[LeadAmp::MAX_CABINET_FRAMES * 2] = 1.0f;
    const auto input = sine(330, 0.1, SAMPLE_RATE / 4);
    REQUIRE(run(input, 1.0f, cabinet) == run(input, 1.0f, impulse(0, 1.0f)));
}

void humPlaysUnderNotesAndFadesAfter() {
    auto input = sine(330, 0.1, SAMPLE_RATE * 5);
    std::fill(input.begin() + SAMPLE_RATE / 2 * CHANNELS, input.end(), 0.0f);
    const auto output = run(input, 1.0f);
    const int frames = 8192;
    const double playing = tonePower(output, SAMPLE_RATE / 4, frames, 180);
    const double closed = tonePower(output, SAMPLE_RATE * 4, frames, 180);
    REQUIRE(playing > 1e-6);
    REQUIRE(closed < 1e-4 * playing);
    REQUIRE(rms(output, 0, SAMPLE_RATE * 4, frames) < 1e-4);
}

std::vector<float> pluck(double frequency, int frames) {
    std::vector<float> samples(static_cast<std::size_t>(frames));
    for (int frame = 0; frame < frames; ++frame) {
        const double time = static_cast<double>(frame) / SAMPLE_RATE;
        double value = 0;
        for (int harmonic = 1; harmonic <= 12; ++harmonic) {
            value += std::sin(2 * PI * frequency * harmonic * time) / harmonic;
        }
        samples[frame] = static_cast<float>(0.1 * std::exp(-4 * time) * value);
    }
    return samples;
}

std::vector<float> monoRender(const std::vector<float>& input, float drive, const Tone& tone) {
    LeadAmp amp(1, SAMPLE_RATE);
    amp.setTone(drive, tone.data());
    auto samples = input;
    for (std::size_t first = 0; first < samples.size(); first += 512) {
        amp.process(samples.data() + first, std::min<std::size_t>(512, samples.size() - first), 1.0f);
    }
    return samples;
}

double punch(const std::vector<float>& samples) {
    double attack = 0;
    for (int first = 0; first + 240 <= SAMPLE_RATE / 20; first += 240) {
        double energy = 0;
        for (int frame = first; frame < first + 240; ++frame) energy += samples[frame] * samples[frame];
        attack = std::max(attack, std::sqrt(energy / 240));
    }
    double body = 0;
    const int bodyStart = SAMPLE_RATE * 8 / 100;
    for (std::size_t frame = bodyStart; frame < samples.size(); ++frame) body += samples[frame] * samples[frame];
    return attack / std::sqrt(body / (samples.size() - bodyStart));
}

struct Matched {
    int result;
    float drive;
    Tone tone;
    float levelGain;
};

Matched match(float drive, const Tone& tone, int noteCount) {
    std::vector<float> stem, di;
    std::vector<std::uint32_t> frames;
    for (int note = 0; note < noteCount; ++note) {
        const auto played = pluck(196 * std::pow(2.0, note / 4.0), SAMPLE_RATE * 2 / 5);
        const auto heard = monoRender(played, drive, tone);
        di.insert(di.end(), played.begin(), played.end());
        stem.insert(stem.end(), heard.begin(), heard.end());
        frames.push_back(static_cast<std::uint32_t>(played.size()));
    }
    Matched matched{};
    matched.result = yarg::audio::leadAmpMatch(SAMPLE_RATE, nullptr, 0, 0, stem.data(), di.data(), frames.data(),
        frames.size(), &matched.drive, matched.tone.data(), matched.tone.size(), &matched.levelGain);
    return matched;
}

void flatToneLeavesSoundUnchanged() {
    const auto input = sine(330, 0.1, SAMPLE_RATE / 2);
    LeadAmp amp(CHANNELS, SAMPLE_RATE);
    auto untouched = input;
    amp.process(untouched.data(), untouched.size() / CHANNELS, 1.0f);
    REQUIRE(run(input, 1.0f) == untouched);
}

void driveSquashesTheAttack() {
    const auto input = pluck(220, SAMPLE_RATE * 2 / 5);
    REQUIRE(punch(monoRender(input, 4.0f, {})) < 0.8 * punch(monoRender(input, 0.25f, {})));
}

void toneBandBoostsItsOctave() {
    Tone tone{};
    tone[4] = 12.0f;
    const auto input = sine(500, 0.1, SAMPLE_RATE / 2);
    const int first = SAMPLE_RATE / 4;
    const int frames = 8192;
    const auto flat = run(input, 1.0f);
    const auto boosted = run(input, 1.0f, {}, 1.0f, tone);
    const double flatRatio = tonePower(flat, first, frames, 1000) / tonePower(flat, first, frames, 500);
    const double boostedRatio = tonePower(boosted, first, frames, 1000) / tonePower(boosted, first, frames, 500);
    REQUIRE(boostedRatio > 4 * flatRatio);
}

void invalidToneIsRejected() {
    Tone tone{};
    REQUIRE(LeadAmp::validTone(1.0f, tone.data()));
    REQUIRE(!LeadAmp::validTone(LeadAmp::MAX_DRIVE * 2, tone.data()));
    tone[0] = LeadAmp::MAX_TONE_DB + 1;
    REQUIRE(!LeadAmp::validTone(1.0f, tone.data()));
    REQUIRE(!LeadAmp::validTone(1.0f, nullptr));
}

void matchKeepsTheDefaultTone() {
    const auto matched = match(1.0f, {}, 8);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    REQUIRE(std::abs(std::log2(matched.drive)) < 0.3);
    for (float band : matched.tone) {
        REQUIRE(std::abs(band) < 2.0f);
    }
    REQUIRE(std::abs(matched.levelGain - 1.0f) < 0.1f);
}

void matchFindsDriveAndTone() {
    Tone tone{};
    tone[2] = -6.0f;
    tone[5] = 6.0f;
    const auto matched = match(3.0f, tone, 8);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    REQUIRE(std::abs(std::log2(matched.drive / 3.0f)) < 0.6);
    REQUIRE(matched.tone[5] - matched.tone[2] > 8.0f);
    REQUIRE(std::isfinite(matched.levelGain) && matched.levelGain > 0.0f);
}

void matchNeedsEnoughNotes() {
    const auto matched = match(1.0f, {}, 2);
    REQUIRE(matched.result == YARG_AUDIO_ERROR_INVALID_STATE);
    REQUIRE(matched.drive == 1.0f);
    REQUIRE(matched.levelGain == 1.0f);
}

}

void runLeadAmpDspTests() {
    silenceStaysSilent();
    addsHarmonics();
    compressesLoudAndQuietNotes();
    gainScalesOutput();
    echoesAfterTheNoteStops();
    channelsEchoAtDifferentTimes();
    staysFiniteWhenOverdriven();
    cabinetDelaysLikeItsImpulse();
    cabinetLevelIsNormalized();
    silentCabinetFallsBackToFilters();
    cabinetShapesTone();
    longCabinetIsTruncated();
    humPlaysUnderNotesAndFadesAfter();
    flatToneLeavesSoundUnchanged();
    driveSquashesTheAttack();
    toneBandBoostsItsOctave();
    invalidToneIsRejected();
    matchKeepsTheDefaultTone();
    matchFindsDriveAndTone();
    matchNeedsEnoughNotes();
}
