#include "stretch/StretchRender.h"
#include "Test.h"

#include <chrono>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <random>
#include <vector>

namespace {

constexpr double PI = 3.14159265358979323846;
constexpr int SAMPLE_RATE = 44100;
constexpr int CHANNELS = 2;
constexpr float SPEED = 0.25f;
constexpr int MAX_STEP_ERROR_INPUT_FRAMES = 256;

std::vector<float> createSteps(int frames, int stepFrames, const std::vector<double>& frequencies) {
    std::vector<float> samples(static_cast<std::size_t>(frames) * CHANNELS);
    double phase = 0;
    for (int frame = 0; frame < frames; ++frame) {
        const double frequency = frequencies[(frame / stepFrames) % frequencies.size()];
        phase += 2 * PI * frequency / SAMPLE_RATE;
        samples[frame * CHANNELS] = static_cast<float>(0.4 * std::sin(phase));
        samples[frame * CHANNELS + 1] = static_cast<float>(0.3 * std::sin(phase));
    }
    return samples;
}

std::vector<float> createGuitarLike(int frames) {
    std::mt19937 random(7);
    std::normal_distribution<float> noise(0.0f, 0.02f);
    std::vector<float> samples(static_cast<std::size_t>(frames) * CHANNELS);
    const double notes[] = {196, 247, 294, 330, 392, 440};
    for (int frame = 0; frame < frames; ++frame) {
        const int note = (frame / (SAMPLE_RATE / 8)) % 6;
        const int age = frame % (SAMPLE_RATE / 8);
        const double envelope = 0.3 * std::exp(-age / (0.2 * SAMPLE_RATE));
        double value = 0;
        for (int harmonic = 1; harmonic <= 8; ++harmonic) {
            value += envelope / harmonic * std::sin(2 * PI * notes[note] * harmonic * frame / SAMPLE_RATE);
        }
        samples[frame * CHANNELS] = static_cast<float>(value) + noise(random);
        samples[frame * CHANNELS + 1] = static_cast<float>(0.8 * value) + noise(random);
    }
    return samples;
}

std::vector<float> renderAll(const std::vector<float>& input, std::int64_t startFrame, int outputFrames,
    float speed = SPEED) {
    const auto inputFrames = static_cast<std::int64_t>(input.size() / CHANNELS);
    yarg::audio::StretchRender render(CHANNELS, SAMPLE_RATE, speed);
    render.begin(input.data(), inputFrames, startFrame);
    std::vector<float> output(static_cast<std::size_t>(outputFrames) * CHANNELS);
    REQUIRE(render.read(input.data(), inputFrames, 0, output.data(), outputFrames));
    return output;
}

double tonePower(const std::vector<float>& output, int centreFrame, int frames, double frequency) {
    double real = 0;
    double imaginary = 0;
    for (int frame = centreFrame - frames / 2; frame < centreFrame + frames / 2; ++frame) {
        const double window = 0.5 - 0.5 * std::cos(2 * PI * (frame - centreFrame + frames / 2) / frames);
        const double sample = output[frame * CHANNELS] * window;
        real += sample * std::cos(2 * PI * frequency * frame / SAMPLE_RATE);
        imaginary += sample * std::sin(2 * PI * frequency * frame / SAMPLE_RATE);
    }
    return real * real + imaginary * imaginary;
}

int findCrossover(const std::vector<float>& output, int firstFrame, int lastFrame, double before, double after) {
    const int analysisFrames = 2048;
    for (int frame = firstFrame; frame < lastFrame; frame += 8) {
        if (tonePower(output, frame, analysisFrames, after) >= tonePower(output, frame, analysisFrames, before)) {
            return frame;
        }
    }
    return -1;
}

void outputFramesMapToStartPlusSpeed(float speed) {
    const int stepFrames = SAMPLE_RATE / 4;
    const std::vector<double> frequencies = {330, 440, 554, 660};
    const auto input = createSteps(SAMPLE_RATE * 2, stepFrames, frequencies);
    const int startFrame = 13230;
    const auto output = renderAll(input, startFrame, SAMPLE_RATE * 4, speed);
    const int searchFrames = static_cast<int>(SAMPLE_RATE / 16 / speed);
    for (int step = 1; step < 6; ++step) {
        const int boundary = step * stepFrames;
        if (boundary <= startFrame) {
            continue;
        }
        const int expected = static_cast<int>((boundary - startFrame) / speed);
        const int found = findCrossover(output, expected - searchFrames, expected + searchFrames,
            frequencies[(step - 1) % frequencies.size()], frequencies[step % frequencies.size()]);
        REQUIRE(found >= 0);
        REQUIRE(std::abs(found - expected) * speed <= MAX_STEP_ERROR_INPUT_FRAMES);
    }
}

void skippedReadsMatchContinuousRender() {
    const auto input = createGuitarLike(SAMPLE_RATE);
    const auto inputFrames = static_cast<std::int64_t>(input.size() / CHANNELS);
    const auto full = renderAll(input, 4410, SAMPLE_RATE * 2);
    yarg::audio::StretchRender render(CHANNELS, SAMPLE_RATE, SPEED);
    render.begin(input.data(), inputFrames, 4410);
    const int sliceFrames = 3000;
    std::vector<float> slice(static_cast<std::size_t>(sliceFrames) * CHANNELS);
    for (int outputFrame : {1000, 20000, 20000 + sliceFrames, 61000}) {
        REQUIRE(render.read(input.data(), inputFrames, outputFrame, slice.data(), sliceFrames));
        for (int sample = 0; sample < sliceFrames * CHANNELS; ++sample) {
            REQUIRE(slice[sample] == full[outputFrame * CHANNELS + sample]);
        }
    }
    REQUIRE(!render.read(input.data(), inputFrames, 0, slice.data(), sliceFrames));
}

void outputStaysFinite() {
    const auto input = createGuitarLike(SAMPLE_RATE / 2);
    const auto output = renderAll(input, 0, SAMPLE_RATE * 3);
    for (float sample : output) {
        REQUIRE(std::isfinite(sample));
        REQUIRE(std::abs(sample) < 4);
    }
}

double harmonicCentroid(const std::vector<float>& output, int centreFrame, double fundamental) {
    double power = 0;
    double weighted = 0;
    for (int harmonic = 1; harmonic * fundamental < 8000; ++harmonic) {
        const double energy = tonePower(output, centreFrame, 4096, fundamental * harmonic);
        power += energy;
        weighted += energy * fundamental * harmonic;
    }
    return weighted / power;
}

void transposeKeepsFormants() {
    const double fundamental = 150;
    const float factor = 1.5f;
    std::vector<float> input(static_cast<std::size_t>(SAMPLE_RATE) * CHANNELS);
    for (int frame = 0; frame < SAMPLE_RATE; ++frame) {
        double value = 0;
        for (int harmonic = 1; harmonic * fundamental < 8000; ++harmonic) {
            const double frequency = fundamental * harmonic;
            const double formant = 0.15 + std::exp(-std::pow((frequency - 900) / 400, 2));
            value += 0.05 * formant / std::pow(harmonic, 0.65) * std::sin(2 * PI * frequency * frame / SAMPLE_RATE);
        }
        input[frame * CHANNELS] = static_cast<float>(value);
        input[frame * CHANNELS + 1] = static_cast<float>(value);
    }

    const auto inputFrames = static_cast<std::int64_t>(input.size() / CHANNELS);
    yarg::audio::StretchRender render(CHANNELS, SAMPLE_RATE, 1.0f);
    render.setPitch(factor, static_cast<float>(fundamental));
    render.begin(input.data(), inputFrames, 0);
    std::vector<float> output(static_cast<std::size_t>(SAMPLE_RATE) * CHANNELS);
    REQUIRE(render.read(input.data(), inputFrames, 0, output.data(), SAMPLE_RATE));

    const int centre = SAMPLE_RATE / 2;
    const double shifted = fundamental * factor;
    REQUIRE(tonePower(output, centre, 4096, shifted) > 10 * tonePower(output, centre, 4096, fundamental));
    REQUIRE(harmonicCentroid(output, centre, shifted) < 1.15 * harmonicCentroid(input, centre, fundamental));
}

void reportSoloRenderTime() {
    const int soloSeconds = 10;
    const auto input = createGuitarLike(SAMPLE_RATE * soloSeconds);
    const auto start = std::chrono::steady_clock::now();
    const auto output = renderAll(input, 0, static_cast<int>(SAMPLE_RATE * soloSeconds / SPEED));
    const auto elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
    std::cout << "StretchRender: " << soloSeconds << " s solo at " << SPEED << "x took "
              << elapsed << " s\n";
    REQUIRE(!output.empty());
}

}

void runStretchRenderTests() {
    outputFramesMapToStartPlusSpeed(1.0f);
    outputFramesMapToStartPlusSpeed(0.5f);
    outputFramesMapToStartPlusSpeed(0.25f);
    skippedReadsMatchContinuousRender();
    outputStaysFinite();
    transposeKeepsFormants();
    reportSoloRenderTime();
}
