#include "dsp/OverdriveDsp.h"
#include "Test.h"

#include <algorithm>
#include <cmath>
#include <vector>

using yarg::audio::Overdrive;

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

double rms(const std::vector<float>& samples, int firstFrame, int frames) {
    double sum = 0;
    for (int frame = firstFrame; frame < firstFrame + frames; ++frame) {
        sum += samples[frame * CHANNELS] * samples[frame * CHANNELS];
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

std::vector<float> run(std::vector<float> samples, float mix, float drive) {
    Overdrive overdrive(CHANNELS, SAMPLE_RATE);
    const std::size_t frames = samples.size() / CHANNELS;
    const std::size_t block = 512;
    for (std::size_t first = 0; first < frames; first += block) {
        overdrive.process(samples.data() + first * CHANNELS, std::min(block, frames - first), mix, drive);
    }
    return samples;
}

void bypassIsExact() {
    const auto input = sine(220, 0.5, SAMPLE_RATE / 4);
    const auto output = run(input, 0.0f, 4.0f);
    for (std::size_t index = 0; index < input.size(); ++index) {
        REQUIRE(output[index] == input[index]);
    }
}

void neverPeaksAboveInput() {
    std::vector<float> input = sine(110, 0.9, SAMPLE_RATE);
    for (int frame = SAMPLE_RATE / 2; frame < SAMPLE_RATE; ++frame) {
        input[frame * CHANNELS] *= 0.05f;
        input[frame * CHANNELS + 1] *= 0.05f;
    }
    const auto output = run(input, 1.0f, Overdrive::MAX_DRIVE);
    float inputPeak = 0;
    for (float sample : input) {
        inputPeak = std::max(inputPeak, std::abs(sample));
    }
    for (float sample : output) {
        REQUIRE(std::isfinite(sample));
        REQUIRE(std::abs(sample) <= inputPeak);
    }
}

void keepsLoudness() {
    const auto input = sine(220, 0.3, SAMPLE_RATE);
    const auto output = run(input, 1.0f, 4.0f);
    const int settled = SAMPLE_RATE / 2;
    const double ratio = rms(output, settled, SAMPLE_RATE / 4) / rms(input, settled, SAMPLE_RATE / 4);
    REQUIRE(ratio > 0.7 && ratio < 1.42);
}

void addsHarmonics() {
    const auto input = sine(220, 0.3, SAMPLE_RATE);
    const auto output = run(input, 1.0f, 4.0f);
    const int first = SAMPLE_RATE / 2;
    const int frames = 8192;
    const double inputRatio = tonePower(input, first, frames, 660) / tonePower(input, first, frames, 220);
    const double outputRatio = tonePower(output, first, frames, 660) / tonePower(output, first, frames, 220);
    REQUIRE(outputRatio > 100 * inputRatio + 1e-6);
}

void attackIsNotLouderThanClean() {
    const int silentFrames = SAMPLE_RATE / 10;
    std::vector<float> input(static_cast<std::size_t>(SAMPLE_RATE / 2) * CHANNELS, 0.0f);
    const auto burst = sine(196, 1.0, SAMPLE_RATE / 2 - silentFrames);
    for (std::size_t index = 0; index < burst.size(); ++index) {
        const double seconds = static_cast<double>(index / CHANNELS) / SAMPLE_RATE;
        input[silentFrames * CHANNELS + index] = static_cast<float>(burst[index] * (0.08 + 0.5 * std::exp(-seconds / 0.01)));
    }
    const auto output = run(input, 1.0f, 15.0f);
    const int window = SAMPLE_RATE / 100;
    double loudestClean = 0;
    for (int first = silentFrames; first + window <= SAMPLE_RATE / 2; first += window) {
        loudestClean = std::max(loudestClean, rms(input, first, window));
        REQUIRE(rms(output, first, window) <= loudestClean);
    }
}

std::vector<float> fadedPluck() {
    const int frames = SAMPLE_RATE / 2;
    const int fadeFrames = SAMPLE_RATE * 4 / 1000;
    std::vector<float> samples(static_cast<std::size_t>(frames) * CHANNELS);
    for (int frame = 0; frame < frames; ++frame) {
        const double seconds = static_cast<double>(frame) / SAMPLE_RATE;
        const double envelope = (0.15 + 0.5 * std::exp(-seconds / 0.03)) * std::min(1.0, frame / static_cast<double>(fadeFrames));
        const auto value = static_cast<float>(envelope * std::sin(2 * PI * 196 * seconds));
        samples[frame * CHANNELS] = value;
        samples[frame * CHANNELS + 1] = value;
    }
    return samples;
}

double steepestStep(const std::vector<float>& samples, int firstFrame, int lastFrame) {
    double steepest = 0;
    for (int frame = firstFrame + 1; frame < lastFrame; ++frame) {
        steepest = std::max(steepest, static_cast<double>(std::abs(samples[frame * CHANNELS] - samples[(frame - 1) * CHANNELS])));
    }
    return steepest;
}

void onsetsDoNotPop() {
    Overdrive overdrive(CHANNELS, SAMPLE_RATE);
    for (int note = 0; note < 2; ++note) {
        auto samples = fadedPluck();
        const std::size_t frames = samples.size() / CHANNELS;
        for (std::size_t first = 0; first < frames; first += 512) {
            overdrive.process(samples.data() + first * CHANNELS, std::min<std::size_t>(512, frames - first), 1.0f, 15.0f);
        }
        const double steady = steepestStep(samples, SAMPLE_RATE / 10, SAMPLE_RATE / 5);
        REQUIRE(steepestStep(samples, 0, SAMPLE_RATE / 100) <= 1.25 * steady);
    }
}

void silenceStaysSilent() {
    const std::vector<float> input(static_cast<std::size_t>(SAMPLE_RATE / 4) * CHANNELS, 0.0f);
    const auto output = run(input, 1.0f, 4.0f);
    for (float sample : output) {
        REQUIRE(sample == 0.0f);
    }
}

}

void runOverdriveDspTests() {
    bypassIsExact();
    neverPeaksAboveInput();
    keepsLoudness();
    addsHarmonics();
    attackIsNotLouderThanClean();
    onsetsDoNotPop();
    silenceStaysSilent();
}
