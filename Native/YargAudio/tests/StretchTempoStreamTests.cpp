#include "stretch/StretchTempoStream.h"
#include "Test.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <complex>
#include <cstdlib>
#include <cstring>
#include <new>
#include <thread>
#include <vector>

namespace {

thread_local bool countAllocations = false;
thread_local std::size_t allocationCount = 0;

}

void* operator new(std::size_t size) {
    if (countAllocations) {
        ++allocationCount;
    }
    if (auto* memory = std::malloc(std::max<std::size_t>(size, 1))) {
        return memory;
    }
    throw std::bad_alloc();
}

void* operator new[](std::size_t size) { return ::operator new(size); }
void operator delete(void* memory) noexcept { std::free(memory); }
void operator delete[](void* memory) noexcept { std::free(memory); }
void operator delete(void* memory, std::size_t) noexcept { std::free(memory); }
void operator delete[](void* memory, std::size_t) noexcept { std::free(memory); }

namespace {

using namespace yarg::audio;

constexpr int SAMPLE_RATE = 44100;
constexpr int CHANNELS = 2;
constexpr int FRAME_BYTES = CHANNELS * sizeof(float);
constexpr double PI = 3.14159265358979323846;

struct Source {
    BassStreamProc callback = nullptr;
    void* user = nullptr;
    std::uint32_t sampleRate = SAMPLE_RATE;
    std::uint32_t channels = CHANNELS;
    std::uint64_t frame = 0;
    std::uint64_t length = SAMPLE_RATE * 4;
    int chunk = 100000;
    int impulse = -1;
    int drum = -1;
    int punch = -1;
    bool noise = false;
    bool dense = false;
    std::vector<float> pcm;
    std::uint32_t noiseSeed = 12345u;
    bool freed = false;
    int locks = 0;
};

Source* source;

std::uint32_t YARG_BASS_CALL getData(std::uint32_t, void* buffer, std::uint32_t bytes) {
    const auto frameBytes = source->channels * sizeof(float);
    if (source->frame == source->length) {
        return UINT32_MAX;
    }
    const auto frames = std::min<std::uint64_t>(
        std::min<int>(bytes / frameBytes, source->chunk), source->length - source->frame);
    auto* samples = static_cast<float*>(buffer);
    for (std::uint64_t frame = 0; frame < frames; ++frame) {
        const auto inputFrame = source->frame++;
        float value;
        if (!source->pcm.empty()) {
            for (std::uint32_t channel = 0; channel < source->channels; ++channel) {
                samples[frame * source->channels + channel] =
                    source->pcm[inputFrame * source->channels + channel];
            }
            continue;
        }
        if (source->impulse >= 0) {
            value = inputFrame == static_cast<std::uint64_t>(source->impulse) ? 1.0f : 0.0f;
        } else if (source->drum >= 0 &&
            inputFrame >= static_cast<std::uint64_t>(source->drum)) {
            const auto struck = inputFrame - static_cast<std::uint64_t>(source->drum);
            const float decay = std::exp(-static_cast<float>(struck) / (0.25f * source->sampleRate));
            value = 0.8f * decay *
                static_cast<float>(std::sin(2 * PI * 90 * struck / source->sampleRate));
            if (struck < 220) {
                const auto hashed = (struck * 1103515245u + 12345u) >> 16 & 0x7fffu;
                value += (hashed / 32768.0f - 0.5f) *
                    std::exp(-static_cast<float>(struck) / 60.0f);
            }
        } else if (source->punch >= 0) {
            float gain = 0.05f;
            if (inputFrame >= static_cast<std::uint64_t>(source->punch)) {
                const auto since = inputFrame - static_cast<std::uint64_t>(source->punch);
                const float fade = std::min(1.0f, static_cast<float>(since) / 220.0f);
                gain = 0.05f + 0.85f * (0.5f - 0.5f * static_cast<float>(std::cos(PI * fade)));
            }
            value = gain * static_cast<float>(std::sin(2 * PI * 110 * inputFrame / source->sampleRate));
        } else if (source->dense) {
            float dense = 0.25f * static_cast<float>(std::sin(2 * PI * 110 * inputFrame / source->sampleRate)) +
                0.2f * static_cast<float>(std::sin(2 * PI * 165 * inputFrame / source->sampleRate)) +
                0.15f * static_cast<float>(std::sin(2 * PI * 220 * inputFrame / source->sampleRate));
            source->noiseSeed = source->noiseSeed * 1103515245u + 12345u;
            dense += 0.08f * static_cast<float>((source->noiseSeed >> 16 & 0x7fffu) / 32768.0f - 0.5f);
            const auto hatPhase = inputFrame % 11025u;
            if (hatPhase < 1323u) {
                source->noiseSeed = source->noiseSeed * 1103515245u + 12345u;
                dense += 0.3f * std::exp(-static_cast<float>(hatPhase) / 200.0f) *
                    static_cast<float>((source->noiseSeed >> 16 & 0x7fffu) / 32768.0f - 0.5f);
            }
            const auto kickPhase = inputFrame % 22050u;
            if (kickPhase < 11025u) {
                dense += 0.8f * std::exp(-static_cast<float>(kickPhase) / 11025.0f) *
                    static_cast<float>(std::sin(2 * PI * 90 * kickPhase / source->sampleRate));
                if (kickPhase < 220u) {
                    source->noiseSeed = source->noiseSeed * 1103515245u + 12345u;
                    dense += static_cast<float>((source->noiseSeed >> 16 & 0x7fffu) / 32768.0f - 0.5f) *
                        std::exp(-static_cast<float>(kickPhase) / 60.0f);
                }
            }
            value = dense;
        } else if (source->noise) {
            source->noiseSeed = source->noiseSeed * 1103515245u + 12345u;
            value = static_cast<float>((source->noiseSeed >> 16 & 0x7fffu) / 32768.0f - 0.5f);
        } else {
            value = static_cast<float>(0.5 * std::sin(2 * PI * 440 * inputFrame / source->sampleRate));
        }
        for (std::uint32_t channel = 0; channel < source->channels; ++channel) {
            samples[frame * source->channels + channel] = value;
        }
    }
    return frames * frameBytes;
}

int YARG_BASS_CALL error() { return 45; }
int YARG_BASS_CALL lockChannel(std::uint32_t, int lock) {
    REQUIRE(!countAllocations);
    source->locks += lock ? 1 : -1;
    return 1;
}
int YARG_BASS_CALL getInfo(std::uint32_t, BassChannelInfo* info) {
    *info = BassChannelInfo{source->sampleRate, source->channels, 0x200100, 0, 0, 0, 0, nullptr};
    return 1;
}
std::uint32_t YARG_BASS_CALL createStream(std::uint32_t frequency,
    std::uint32_t channels, std::uint32_t flags, BassStreamProc callback, void* user) {
    REQUIRE(frequency == source->sampleRate);
    REQUIRE(channels == source->channels);
    REQUIRE(flags == 0x200100);
    source->callback = callback;
    source->user = user;
    return 19;
}
int YARG_BASS_CALL freeStream(std::uint32_t stream) {
    REQUIRE(stream == 19);
    source->freed = true;
    return 1;
}

BassCoreBindings makeBass(Source& state) {
    source = &state;
    BassCoreFunctions functions{};
    functions.channelGetData = &getData;
    functions.errorGetCode = &error;
    functions.channelLock = &lockChannel;
    functions.channelGetInfo = &getInfo;
    functions.streamCreate = &createStream;
    functions.streamFree = &freeStream;
    return BassCoreBindings(functions);
}

std::vector<float> render(Source& state, int chunk = 997) {
    std::vector<float> output;
    std::vector<float> block(chunk * state.channels);
    while (true) {
        countAllocations = true;
        const auto result = state.callback(19, block.data(), block.size() * sizeof(float), state.user);
        countAllocations = false;
        REQUIRE(allocationCount == 0);
        const auto samples = (result & 0x7fffffff) / sizeof(float);
        output.insert(output.end(), block.begin(), block.begin() + samples);
        if (result & 0x80000000) {
            return output;
        }
        REQUIRE(output.size() < SAMPLE_RATE * 100u * CHANNELS);
    }
}

void testGainIndependentPhase() {
    for (float speed : {0.2f, 0.3f, 0.35f, 0.5f, 1.0f, 1.5f}) {
        for (float pitch : {1.0f, 1.25f}) {
            std::vector<float> reference;
            for (float gain : {1.0f, 0.25f, 0.00390625f}) {
                Source state;
                state.length = SAMPLE_RATE;
                state.pcm.resize(state.length * CHANNELS);
                for (std::uint64_t frame = 0; frame < state.length; ++frame) {
                    const double time = static_cast<double>(frame) / SAMPLE_RATE;
                    const float value = gain * static_cast<float>(
                        0.3 * std::sin(2 * PI * (220 * time + 40 * time * time)) +
                        0.2 * std::sin(2 * PI * 331 * time));
                    state.pcm[frame * CHANNELS] = value;
                    state.pcm[frame * CHANNELS + 1] = -value * 0.5f;
                }
                auto bass = makeBass(state);
                int errorCode;
                auto stream = StretchTempoStream::create(bass, 11, &errorCode);
                REQUIRE(stream);
                stream->setSpeed(speed, pitch);
                const auto output = render(state);
                if (gain == 1.0f) {
                    reference = output;
                    continue;
                }
                REQUIRE(output.size() == reference.size());
                double error = 0;
                double energy = 0;
                for (std::size_t i = 0; i < output.size(); ++i) {
                    const double difference = output[i] / gain - reference[i];
                    error += difference * difference;
                    energy += reference[i] * reference[i];
                }
                const double relativeError = std::sqrt(error / energy);
                std::printf("gain invariance speed %.2f pitch %.2f gain %.6f: error=%.6f\n",
                    speed, pitch, gain, relativeError);
                REQUIRE(relativeError < 0.01);
            }
        }
    }
}

void testRatesPitchAndTail() {
    for (float speed : {0.05f, 0.5f, 1.0f, 1.25f, 2.0f, 51.0f}) {
        for (float pitch : {1.0f, 1.5f}) {
            Source state;
            state.chunk = 113;
            auto bass = makeBass(state);
            int errorCode = -1;
            auto stream = StretchTempoStream::create(bass, 11, &errorCode);
            REQUIRE(stream);
            stream->setSpeed(speed, pitch);
            const auto output = render(state);
            const auto frames = output.size() / CHANNELS;
            REQUIRE(std::abs(static_cast<double>(frames) - state.length / speed) < 22);
            for (std::size_t i = 0; i < output.size(); i += CHANNELS) {
                REQUIRE(std::isfinite(output[i]));
                REQUIRE(std::abs(output[i] - output[i + 1]) < 0.002f);
            }
            if (speed >= 0.5f && speed <= 2.0f) {
                int crossings = 0;
                const auto begin = frames / 4;
                const auto end = 3 * frames / 4;
                for (auto i = begin + 1; i < end; ++i) {
                    if (output[(i - 1) * CHANNELS] < 0 && output[i * CHANNELS] >= 0) {
                        ++crossings;
                    }
                }
                const double frequency = static_cast<double>(crossings) * SAMPLE_RATE / (end - begin);
                REQUIRE(std::abs(frequency - 440 * pitch) < 4);
            }
            if (speed == 0.05f) {
                REQUIRE(stream->protectedTransients() == 0);
            }
            stream.reset();
            REQUIRE(state.freed);
        }
    }
}

void testSlowPitchStability() {
    for (int sampleRate : {44100, 48000}) {
        for (float speed : {0.1f, 0.2f, 0.3f, 0.33f, 0.35f, 0.4f}) {
            Source state;
            state.sampleRate = sampleRate;
            state.length = sampleRate * 2;
            state.pcm.resize(state.length * CHANNELS);
            for (std::uint64_t frame = 0; frame < state.length; ++frame) {
                const double time = static_cast<double>(frame) / sampleRate;
                const float value = static_cast<float>(
                    0.5 * std::sin(2 * PI * (220 * time + 40 * time * time)));
                state.pcm[frame * CHANNELS] = value;
                state.pcm[frame * CHANNELS + 1] = -0.5f * value;
            }
            auto bass = makeBass(state);
            int errorCode;
            auto stream = StretchTempoStream::create(bass, 11, &errorCode);
            REQUIRE(stream);
            stream->setSpeed(speed, 1);
            const auto output = render(state);
            const auto frames = output.size() / CHANNELS;
            REQUIRE(std::abs(static_cast<double>(frames) - state.length / speed) < 22);
            for (std::size_t frame = 0; frame < frames; ++frame) {
                const float left = output[frame * CHANNELS];
                const float right = output[frame * CHANNELS + 1];
                REQUIRE(std::isfinite(left));
                REQUIRE(std::isfinite(right));
                REQUIRE(std::abs(right + 0.5f * left) < 0.002f);
            }
            std::vector<double> crossings;
            for (std::size_t frame = sampleRate; frame < frames - sampleRate / 2; ++frame) {
                const double previous = output[(frame - 1) * CHANNELS];
                const double current = output[frame * CHANNELS];
                if (previous < 0 && current >= 0) {
                    crossings.push_back(frame - current / (current - previous));
                }
            }
            REQUIRE(crossings.size() > 100);
            double errorSum = 0;
            double errorSquares = 0;
            for (std::size_t i = 1; i < crossings.size(); ++i) {
                const double frequency = sampleRate / (crossings[i] - crossings[i - 1]);
                const double time = (crossings[i] + crossings[i - 1]) / (2 * sampleRate);
                const double expected = 220 + 80 * speed * time;
                const double error = frequency - expected;
                errorSum += error;
                errorSquares += error * error;
            }
            const auto count = crossings.size() - 1;
            const double bias = errorSum / count;
            const double variation = std::sqrt(std::max(0.0,
                errorSquares / count - bias * bias));
            std::printf("slow chirp rate %d speed %.2f: bias=%.4f Hz variation=%.4f Hz\n",
                sampleRate, speed, bias, variation);
            REQUIRE(std::abs(bias) < 0.01);
            REQUIRE(variation < 0.08);
        }
    }
}

void testSlowChordStability() {
    for (float speed : {0.1f, 0.2f, 0.3f, 0.35f, 0.5f, 1.0f}) {
        Source state;
        state.length = SAMPLE_RATE * 2;
        state.pcm.resize(state.length * CHANNELS);
        for (std::uint64_t frame = 0; frame < state.length; ++frame) {
            const double time = static_cast<double>(frame) / SAMPLE_RATE;
            const float value = static_cast<float>(0.3 * std::sin(2 * PI * 220 * time) +
                0.2 * std::sin(2 * PI * 330 * time) + 0.1 * std::sin(2 * PI * 440 * time));
            state.pcm[frame * CHANNELS] = value;
            state.pcm[frame * CHANNELS + 1] = value;
        }
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(speed, 1);
        const auto output = render(state);
        const auto frames = output.size() / CHANNELS;
        constexpr int WINDOW_FRAMES = SAMPLE_RATE / 10;
        double totalEnergy = 0;
        double residualEnergy = 0;
        int measuredFrames = 0;
        for (std::size_t start = SAMPLE_RATE;
            start + WINDOW_FRAMES < frames - SAMPLE_RATE / 2; start += WINDOW_FRAMES) {
            double energy = 0;
            for (int i = 0; i < WINDOW_FRAMES; ++i) {
                const double value = output[(start + i) * CHANNELS];
                energy += value * value;
            }
            double tonalEnergy = 0;
            for (double frequency : {220.0, 330.0, 440.0}) {
                double real = 0;
                double imaginary = 0;
                for (int i = 0; i < WINDOW_FRAMES; ++i) {
                    const double phase = 2 * PI * frequency * i / SAMPLE_RATE;
                    const double value = output[(start + i) * CHANNELS];
                    real += value * std::cos(phase);
                    imaginary += value * std::sin(phase);
                }
                tonalEnergy += 2 * (real * real + imaginary * imaginary) / WINDOW_FRAMES;
            }
            totalEnergy += energy;
            residualEnergy += std::max(0.0, energy - tonalEnergy);
            measuredFrames += WINDOW_FRAMES;
        }
        REQUIRE(measuredFrames > 0);
        REQUIRE(std::abs(totalEnergy / measuredFrames - 0.07) < 0.0035);
        const double residual = std::sqrt(residualEnergy / totalEnergy);
        std::printf("slow chord speed %.2f: residual=%.6f\n", speed, residual);
        REQUIRE(residual < 0.003);
    }
}

void testSlowHighToneStability() {
    for (float speed : {0.1f, 0.2f, 0.3f, 0.35f, 0.5f, 1.0f}) {
        Source state;
        state.length = SAMPLE_RATE * 2;
        state.pcm.resize(state.length * CHANNELS);
        for (std::uint64_t frame = 0; frame < state.length; ++frame) {
            const double time = static_cast<double>(frame) / SAMPLE_RATE;
            const float value = static_cast<float>(0.3 * std::sin(2 * PI * 3000 * time) +
                0.2 * std::sin(2 * PI * 4500 * time) + 0.1 * std::sin(2 * PI * 6000 * time));
            state.pcm[frame * CHANNELS] = value;
            state.pcm[frame * CHANNELS + 1] = value;
        }
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(speed, 1);
        const auto output = render(state);
        const auto frames = output.size() / CHANNELS;
        constexpr int WINDOW_FRAMES = SAMPLE_RATE / 10;
        double totalEnergy = 0;
        double residualEnergy = 0;
        int measuredFrames = 0;
        for (std::size_t start = SAMPLE_RATE;
            start + WINDOW_FRAMES < frames - SAMPLE_RATE / 2; start += WINDOW_FRAMES) {
            double energy = 0;
            for (int i = 0; i < WINDOW_FRAMES; ++i) {
                const double value = output[(start + i) * CHANNELS];
                energy += value * value;
            }
            double tonalEnergy = 0;
            for (double frequency : {3000.0, 4500.0, 6000.0}) {
                double real = 0;
                double imaginary = 0;
                for (int i = 0; i < WINDOW_FRAMES; ++i) {
                    const double phase = 2 * PI * frequency * i / SAMPLE_RATE;
                    const double value = output[(start + i) * CHANNELS];
                    real += value * std::cos(phase);
                    imaginary += value * std::sin(phase);
                }
                tonalEnergy += 2 * (real * real + imaginary * imaginary) / WINDOW_FRAMES;
            }
            totalEnergy += energy;
            residualEnergy += std::max(0.0, energy - tonalEnergy);
            measuredFrames += WINDOW_FRAMES;
        }
        REQUIRE(measuredFrames > 0);
        REQUIRE(std::abs(totalEnergy / measuredFrames - 0.07) < 0.0035);
        const double residual = std::sqrt(residualEnergy / totalEnergy);
        std::printf("slow high tone speed %.2f: residual=%.6f\n", speed, residual);
        REQUIRE(residual < 0.001);
    }
}

void testAdaptiveSidelobeLock() {
    constexpr double FREQUENCIES[] = {220, 2200, 6100};
    for (int sampleRate : {44100, 48000}) {
        Source state;
        state.sampleRate = sampleRate;
        state.length = sampleRate * 3;
        state.pcm.resize(state.length * CHANNELS);
        std::uint32_t seed = 7723;
        for (std::uint64_t frame = 0; frame < state.length; ++frame) {
            const double time = static_cast<double>(frame) / sampleRate;
            const double attackTime = std::fmod(time, 0.6) - 0.08;
            const double envelope = 0.1 + 0.9 * std::exp(-attackTime * attackTime / (2 * 0.025 * 0.025));
            double tone = 0;
            for (double frequency : FREQUENCIES) {
                tone += 0.15 * std::sin(2 * PI * frequency * time);
            }
            for (int channel = 0; channel < CHANNELS; ++channel) {
                seed = seed * 1103515245u + 12345u;
                const double noise = (static_cast<double>(seed >> 16 & 0x7fffu) / 32768 - 0.5) * 0.3;
                state.pcm[frame * CHANNELS + channel] = static_cast<float>(tone + envelope * noise);
            }
        }
        for (float speed : {0.1f, 0.25f, 0.5f, 0.8f, 1.0f, 1.5f}) {
            std::vector<float> outputs[2];
            double positions[2]{};
            int transients[2]{};
            int latencies[2]{};
            for (int adaptive = 0; adaptive < 2; ++adaptive) {
                state.frame = 0;
                auto bass = makeBass(state);
                StretchConfig config;
                config.adaptiveSidelobeLock = adaptive != 0;
                int errorCode;
                auto stream = StretchTempoStream::create(bass, 11, &errorCode, config);
                REQUIRE(stream);
                stream->setSpeed(speed, 1);
                outputs[adaptive] = render(state);
                const auto lastFrame = outputs[adaptive].size() / CHANNELS - 1;
                REQUIRE(stream->position(lastFrame, positions[adaptive]));
                transients[adaptive] = stream->protectedTransients();
                latencies[adaptive] = stream->latencyFrames();
                if (adaptive == 1 && speed == 0.5f) {
                    state.frame = 0;
                    REQUIRE(stream->flush());
                    REQUIRE(render(state, 103) == outputs[adaptive]);
                }
            }
            REQUIRE(outputs[0].size() == outputs[1].size());
            REQUIRE(positions[0] == positions[1]);
            REQUIRE(transients[0] == transients[1]);
            REQUIRE(latencies[0] == latencies[1]);
            if (speed >= 1) {
                REQUIRE(outputs[0] == outputs[1]);
                continue;
            }
            double differencePower = 0;
            double baselinePower = 0;
            for (std::size_t i = 0; i < outputs[0].size(); ++i) {
                const double difference = outputs[1][i] - outputs[0][i];
                REQUIRE(std::isfinite(outputs[1][i]));
                differencePower += difference * difference;
                baselinePower += outputs[0][i] * outputs[0][i];
            }
            REQUIRE(differencePower > 0);
            REQUIRE(differencePower < baselinePower * 0.0001);
            const int window = sampleRate / 10;
            const int hop = window / 2;
            const auto frames = outputs[0].size() / CHANNELS;
            for (double frequency : FREQUENCIES) {
                double previousPhase = 0;
                double errorSquares = 0;
                double maxError = 0;
                int measured = 0;
                for (std::size_t start = sampleRate / 2; start + window < frames - sampleRate / 2; start += hop) {
                    std::complex<double> tones[2]{};
                    for (int i = 0; i < window; ++i) {
                        const double weight = 0.5 - 0.5 * std::cos(2 * PI * i / (window - 1));
                        const auto oscillator = std::polar(weight, -2 * PI * frequency * i / sampleRate);
                        for (int adaptive = 0; adaptive < 2; ++adaptive) {
                            tones[adaptive] += static_cast<double>(outputs[adaptive][(start + i) * CHANNELS]) * oscillator;
                        }
                    }
                    const double phase = std::arg(tones[1] * std::conj(tones[0]));
                    if (start > static_cast<std::size_t>(sampleRate / 2)) {
                        const double delta = std::remainder(phase - previousPhase, 2 * PI);
                        const double frequencyError = delta * sampleRate / (2 * PI * hop);
                        const double cents = 1200 * std::log2(1 + frequencyError / frequency);
                        errorSquares += cents * cents;
                        maxError = std::max(maxError, std::abs(cents));
                        ++measured;
                    }
                    previousPhase = phase;
                }
                REQUIRE(measured > 0);
                const double rmsError = std::sqrt(errorSquares / measured);
                std::printf("adaptive mixed rate %d speed %.2f frequency %.0f: added-rms=%.6f cents max=%.6f cents\n",
                    sampleRate, speed, frequency, rmsError, maxError);
                REQUIRE(rmsError < 0.05);
                REQUIRE(maxError < 0.25);
            }
        }
        for (float pitch : {0.75f, 1.25f}) {
            std::vector<float> outputs[2];
            for (int adaptive = 0; adaptive < 2; ++adaptive) {
                state.frame = 0;
                auto bass = makeBass(state);
                StretchConfig config;
                config.adaptiveSidelobeLock = adaptive != 0;
                int errorCode;
                auto stream = StretchTempoStream::create(bass, 11, &errorCode, config);
                REQUIRE(stream);
                stream->setSpeed(0.5f, pitch);
                outputs[adaptive] = render(state);
            }
            REQUIRE(outputs[0] == outputs[1]);
        }
    }
}

void testAdaptiveSidelobeTransitions() {
    Source state;
    state.length = SAMPLE_RATE * 3;
    state.pcm.resize(state.length * CHANNELS);
    std::uint32_t seed = 8123;
    for (std::uint64_t frame = 0; frame < state.length; ++frame) {
        const double time = static_cast<double>(frame) / SAMPLE_RATE;
        for (int channel = 0; channel < CHANNELS; ++channel) {
            seed = seed * 1103515245u + 12345u;
            const double noise = (static_cast<double>(seed >> 16 & 0x7fffu) / 32768 - 0.5) * 0.3;
            state.pcm[frame * CHANNELS + channel] = time < 1 ? static_cast<float>(noise) :
                (time < 2 ? 0 : static_cast<float>(0.3 * std::sin(2 * PI * 2200 * time)));
        }
    }
    std::vector<float> outputs[2];
    std::vector<double> positions[2];
    for (int adaptive = 0; adaptive < 2; ++adaptive) {
        state.frame = 0;
        auto bass = makeBass(state);
        StretchConfig config;
        config.adaptiveSidelobeLock = adaptive != 0;
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode, config);
        REQUIRE(stream);
        stream->setSpeed(0.5f, 1);
        float block[StretchTempoStream::BLOCK_FRAMES * CHANNELS];
        for (int step = 0; ; ++step) {
            if (step == 100) {
                stream->setSpeed(0.5f, 1.25f);
            } else if (step == 140) {
                stream->setSpeed(0.5f, 1);
            } else if (step == 180) {
                stream->setSpeed(1, 1);
            } else if (step == 220) {
                stream->setSpeed(0.5f, 1);
            } else if (step == 450) {
                stream->setSpeed(0.25f, 1);
            } else if (step == 500) {
                stream->setSpeed(0.8f, 1);
            }
            countAllocations = true;
            const auto result = state.callback(19, block, sizeof(block), state.user);
            countAllocations = false;
            REQUIRE(allocationCount == 0);
            const auto samples = (result & 0x7fffffff) / sizeof(float);
            REQUIRE(std::all_of(block, block + samples, [](float value) { return std::isfinite(value); }));
            if (samples > 0) {
                double position;
                REQUIRE(stream->position(outputs[adaptive].size() / CHANNELS, position));
                positions[adaptive].push_back(position);
                outputs[adaptive].insert(outputs[adaptive].end(), block, block + samples);
            }
            if (result & 0x80000000) {
                break;
            }
            REQUIRE(step < SAMPLE_RATE);
        }
    }
    REQUIRE(outputs[0].size() == outputs[1].size());
    REQUIRE(positions[0] == positions[1]);
    double tailDifference = 0;
    double tailBaseline = 0;
    for (std::size_t block = 0; block < positions[0].size(); ++block) {
        if (positions[0][block] > 2.2) {
            const auto start = block * StretchTempoStream::BLOCK_FRAMES * CHANNELS;
            const auto end = std::min(outputs[0].size(), start + StretchTempoStream::BLOCK_FRAMES * CHANNELS);
            for (auto i = start; i < end; ++i) {
                const double difference = outputs[1][i] - outputs[0][i];
                tailDifference += difference * difference;
                tailBaseline += outputs[0][i] * outputs[0][i];
            }
        }
    }
    REQUIRE(tailBaseline > 0);
    REQUIRE(tailDifference < tailBaseline * 0.0001);
    std::printf("adaptive transitions: identical positions and near-identical post-silence tonal output\n");
}

void testVibratoTransientSelectivity() {
    for (int sampleRate : {44100, 48000}) {
        for (float speed : {0.2f, 0.35f}) {
            for (double frequency : {1480.0, 4480.0, 8950.0}) {
                for (bool addAttack : {false, true}) {
                    Source state;
                    state.sampleRate = sampleRate;
                    state.length = sampleRate * 3;
                    state.pcm.resize(state.length * CHANNELS);
                    std::uint32_t noise = 12345;
                    for (std::uint64_t frame = 0; frame < state.length; ++frame) {
                        const double time = static_cast<double>(frame) / sampleRate;
                        float value = static_cast<float>(0.5 * std::sin(
                            2 * PI * frequency * time - 20 * std::cos(2 * PI * 5 * time)));
                        if (addAttack && frame >= static_cast<std::uint64_t>(sampleRate) &&
                            frame < static_cast<std::uint64_t>(sampleRate + sampleRate / 20)) {
                            noise = noise * 1103515245u + 12345u;
                            const double since = time - 1;
                            value += static_cast<float>(0.6 * std::exp(-since / 0.005) *
                                (static_cast<double>(noise >> 16 & 0x7fffu) / 16384 - 1));
                        }
                        state.pcm[frame * CHANNELS] = value;
                        state.pcm[frame * CHANNELS + 1] = -0.5f * value;
                    }
                    auto bass = makeBass(state);
                    int errorCode;
                    auto stream = StretchTempoStream::create(bass, 11, &errorCode);
                    REQUIRE(stream);
                    stream->setSpeed(speed, 1);
                    const auto output = render(state);
                    const auto frames = output.size() / CHANNELS;
                    REQUIRE(std::abs(static_cast<double>(frames) - state.length / speed) < 22);
                    std::printf("vibrato rate %d speed %.2f frequency %.0f attack %d: protected=%d\n",
                        sampleRate, speed, frequency, addAttack, stream->protectedTransients());
                    if (addAttack) {
                        REQUIRE(stream->protectedTransients() > 0);
                    } else {
                        REQUIRE(stream->protectedTransients() == 0);
                    }
                    double maxStride = 0;
                    for (std::size_t frame = frames / 4; frame < frames; frame += 256) {
                        double actual;
                        double ideal;
                        REQUIRE(stream->position(frame, actual));
                        REQUIRE(stream->idealPosition(frame, ideal));
                        maxStride = std::max(maxStride, std::abs(actual - ideal));
                    }
                    if (addAttack) {
                        REQUIRE(maxStride > 0.001);
                    } else {
                        REQUIRE(maxStride == 0);
                    }
                }
            }
        }
    }
}

void testAlignmentAndReset() {
    Source state;
    state.impulse = SAMPLE_RATE;
    auto bass = makeBass(state);
    int errorCode;
    auto stream = StretchTempoStream::create(bass, 11, &errorCode);
    auto output = render(state, 103);
    auto peak = std::max_element(output.begin(), output.end());
    REQUIRE(std::abs((peak - output.begin()) / CHANNELS - state.impulse) <= 1);
    REQUIRE(*peak > 0.8f);
    state.frame = 0;
    REQUIRE(stream->flush());
    REQUIRE(state.locks == 0);
    auto second = render(state, 2048);
    REQUIRE(output == second);
}

void testPositionHistoryAndTransitions() {
    Source state;
    state.length = SAMPLE_RATE * 30;
    auto bass = makeBass(state);
    int errorCode;
    auto stream = StretchTempoStream::create(bass, 11, &errorCode);
    std::vector<float> output(4410 * CHANNELS);
    state.callback(19, output.data(), output.size() * sizeof(float), state.user);
    double before = 0;
    REQUIRE(stream->getPosition(2000 * FRAME_BYTES, before));
    REQUIRE(std::abs(before - 2000.0 / SAMPLE_RATE) < 1e-9);
    stream->setSpeed(2, 1);
    state.callback(19, output.data(), output.size() * sizeof(float), state.user);
    double after = 0;
    REQUIRE(stream->getPosition(2000 * FRAME_BYTES, after));
    REQUIRE(before == after);
    stream->setSpeed(0.5f, 1);
    state.callback(19, output.data(), output.size() * sizeof(float), state.user);
    double previous = 0;
    for (int frame = 0; frame < 13230; ++frame) {
        double seconds;
        REQUIRE(stream->position(frame, seconds));
        REQUIRE(seconds >= previous);
        REQUIRE(seconds - previous <= 2.0 / SAMPLE_RATE + 1e-9);
        previous = seconds;
    }
    double rejected;
    REQUIRE(!stream->getPosition(-FRAME_BYTES, rejected));
    REQUIRE(stream->flush());
    REQUIRE(stream->getPosition(0, rejected));
    REQUIRE(rejected == 0);
    REQUIRE(!stream->getPosition(FRAME_BYTES, rejected));
}

}

void testConcurrentPositionQueries() {
    Source state;
    state.length = SAMPLE_RATE * 10;
    auto bass = makeBass(state);
    int errorCode;
    auto stream = StretchTempoStream::create(bass, 11, &errorCode);
    REQUIRE(stream);
    std::atomic<std::uint64_t> renderedFrames{0};
    std::atomic<bool> done{false};
    std::atomic<bool> failed{false};
    std::thread querier([&] {
        double previous = 0;
        while (!done.load(std::memory_order_acquire)) {
            const auto rendered = renderedFrames.load(std::memory_order_acquire);
            if (rendered == 0) {
                std::this_thread::yield();
                continue;
            }
            double seconds = -1;
            if (stream->getPosition(
                static_cast<std::int64_t>((rendered - 1) * FRAME_BYTES), seconds)) {
                if (!(seconds >= previous) || !(seconds <= 10.0)) {
                    failed.store(true, std::memory_order_release);
                    return;
                }
                previous = seconds;
            }
            std::this_thread::yield();
        }
    });
    std::vector<float> block(997 * CHANNELS);
    std::uint64_t delivered = 0;
    while (true) {
        const auto result = state.callback(19, block.data(),
            block.size() * sizeof(float), state.user);
        delivered += (result & 0x7fffffff) / FRAME_BYTES;
        renderedFrames.store(delivered, std::memory_order_release);
        if (result & 0x80000000) {
            break;
        }
        REQUIRE(delivered < SAMPLE_RATE * 100u);
    }
    done.store(true, std::memory_order_release);
    querier.join();
    REQUIRE(!failed.load(std::memory_order_acquire));
    stream.reset();
}

void testTransientStepResponse() {
    const int stepFrame = SAMPLE_RATE * 5;
    {
        Source latencyProbe;
        auto latencyBass = makeBass(latencyProbe);
        int latencyError = -1;
        auto latencyStream = StretchTempoStream::create(latencyBass, 11, &latencyError);
        REQUIRE(latencyStream);
        std::printf("stretch latencyFrames=%d block=%d\n",
            latencyStream->latencyFrames(), StretchTempoStream::BLOCK_FRAMES);
    }
    for (float after : {1.0f, 1.02f, 1.05f, 0.95f}) {
        for (double k : {0.05, 0.1, 0.25, 0.5, 1.0}) {
            Source state;
            state.length = SAMPLE_RATE * 10;
            const auto impulse = static_cast<int>(stepFrame + k * SAMPLE_RATE);
            state.impulse = impulse;
            auto bass = makeBass(state);
            int errorCode;
            auto stream = StretchTempoStream::create(bass, 11, &errorCode);
            REQUIRE(stream);
            std::vector<float> output;
            std::vector<float> block(StretchTempoStream::BLOCK_FRAMES * CHANNELS);
            bool stepped = false;
            std::uint64_t delivered = 0;
            const auto renderStart = std::chrono::steady_clock::now();
            while (true) {
                const auto result = state.callback(19, block.data(),
                    block.size() * sizeof(float), state.user);
                const auto frames = (result & 0x7fffffff) / FRAME_BYTES;
                output.insert(output.end(), block.begin(), block.begin() + frames * CHANNELS);
                delivered += frames;
                if (!stepped && delivered >= static_cast<std::uint64_t>(stepFrame)) {
                    stream->setSpeed(after, 1);
                    stepped = true;
                }
                if (result & 0x80000000) {
                    break;
                }
                REQUIRE(output.size() < SAMPLE_RATE * 100u * CHANNELS);
            }
            REQUIRE(stepped);
            const auto peak = std::max_element(output.begin(), output.end());
            REQUIRE(peak != output.end() && *peak > 0.15f);
            const auto peakFrame = static_cast<double>(peak - output.begin()) / CHANNELS;
            double measured = 0;
            REQUIRE(stream->position(peakFrame, measured));
            const double errorMs = (measured - impulse / static_cast<double>(SAMPLE_RATE)) * 1000.0;
            const double renderMs = std::chrono::duration_cast<std::chrono::microseconds>(
                std::chrono::steady_clock::now() - renderStart).count() / 1000.0;
            const double outputSeconds = output.size() / (CHANNELS * static_cast<double>(SAMPLE_RATE));
            std::printf("step to %.2f K=%.2fs: err=%+.2fms render=%.0fms (%.1fx realtime)\n",
                after, k, errorMs, renderMs, outputSeconds * 1000.0 / renderMs);
            REQUIRE(std::abs(errorMs) < 3.0);
        }
    }
}

void testTransientSharpness() {
    for (float speed : {0.25f, 0.5f}) {
        Source state;
        state.length = SAMPLE_RATE * 2;
        state.impulse = SAMPLE_RATE;
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(speed, 1);
        const auto output = render(state);
        const auto frames = output.size() / CHANNELS;
        auto peak = output.begin();
        for (auto i = output.begin(); i < output.end(); ++i) {
            if (std::abs(*i) > std::abs(*peak)) {
                peak = i;
            }
        }
        REQUIRE(std::abs(*peak) > 0.15f);
        const auto peakFrame = static_cast<std::uint64_t>((peak - output.begin()) / CHANNELS);
        double measured = 0;
        REQUIRE(stream->position(static_cast<double>(peakFrame), measured));
        const double timingMs =
            (measured - state.impulse / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::uint64_t left = peakFrame;
        while (left > 0 &&
            std::abs(output[(left - 1) * CHANNELS]) > std::abs(*peak) * 0.1f) {
            --left;
        }
        std::uint64_t right = peakFrame;
        while (right + 1 < frames &&
            std::abs(output[(right + 1) * CHANNELS]) > std::abs(*peak) * 0.1f) {
            ++right;
        }
        const double widthMs = (right - left + 1) * 1000.0 / SAMPLE_RATE;
        std::printf("impulse speed %.2f: peak=%.3f width10=%.1fms timing=%+.1fms\n",
            speed, std::abs(*peak), widthMs, timingMs);
        REQUIRE(widthMs < 10);
        REQUIRE(std::abs(timingMs) < 10);
        stream.reset();
    }
}

void testDrumTransient() {
    for (float speed : {0.25f, 0.5f}) {
        Source state;
        state.length = SAMPLE_RATE * 5 / 2;
        state.drum = SAMPLE_RATE;
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(speed, 1);
        const auto output = render(state);
        const auto frames = output.size() / CHANNELS;
        auto peak = output.begin();
        for (auto i = output.begin(); i < output.end(); ++i) {
            if (std::abs(*i) > std::abs(*peak)) {
                peak = i;
            }
        }
        REQUIRE(std::abs(*peak) > 0.4f);
        const auto peakFrame = static_cast<std::uint64_t>((peak - output.begin()) / CHANNELS);
        double measured = 0;
        REQUIRE(stream->position(static_cast<double>(peakFrame), measured));
        const double timingMs =
            (measured - state.drum / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::uint64_t left = peakFrame;
        while (left > 0 &&
            std::abs(output[(left - 1) * CHANNELS]) > std::abs(*peak) * 0.5f) {
            --left;
        }
        std::uint64_t right = peakFrame;
        while (right + 1 < frames &&
            std::abs(output[(right + 1) * CHANNELS]) > std::abs(*peak) * 0.5f) {
            ++right;
        }
        std::uint64_t tail = peakFrame;
        const auto tailEnd =
            std::min<std::uint64_t>(frames, peakFrame + SAMPLE_RATE * 4u);
        for (auto f = peakFrame; f < tailEnd; ++f) {
            if (std::abs(output[f * CHANNELS]) > std::abs(*peak) * 0.1f) {
                tail = f;
            }
        }
        const double widthMs = (right - left + 1) * 1000.0 / SAMPLE_RATE;
        const double tailMs = (tail - peakFrame) * 1000.0 / SAMPLE_RATE;
        std::uint64_t onset = peakFrame;
        while (onset > 0 &&
            std::abs(output[(onset - 1) * CHANNELS]) > std::abs(*peak) * 0.25f) {
            --onset;
        }
        double onsetMeasured = 0;
        REQUIRE(stream->position(static_cast<double>(onset), onsetMeasured));
        const double onsetMs =
            (onsetMeasured - state.drum / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::printf("drum speed %.2f: peak=%.3f width50=%.1fms tail10=%.0fms timing=%+.1fms onset=%+.1fms\n",
            speed, std::abs(*peak), widthMs, tailMs, timingMs, onsetMs);
        REQUIRE(widthMs < 40);
        REQUIRE(tailMs > 150 / speed && tailMs < 700 / speed);
        REQUIRE(std::abs(timingMs) < 30);
        REQUIRE(std::abs(onsetMs) < 15);
        stream.reset();
    }
}

void testVerySlowTransients() {
    for (float speed : {0.1f}) {
        Source drumState;
        drumState.length = SAMPLE_RATE * 6 / 5;
        drumState.drum = SAMPLE_RATE * 3 / 10;
        auto drumBass = makeBass(drumState);
        int drumError;
        auto drumStream = StretchTempoStream::create(drumBass, 11, &drumError);
        REQUIRE(drumStream);
        drumStream->setSpeed(speed, 1);
        const auto drumOutput = render(drumState);
        const auto drumFrames = drumOutput.size() / CHANNELS;
        const auto drumExpected =
            static_cast<std::uint64_t>(drumState.drum / static_cast<double>(speed));
        const auto drumSearchStart = drumExpected > SAMPLE_RATE / 2 ?
            drumExpected - SAMPLE_RATE / 2 : 0;
        const auto drumSearchEnd =
            std::min<std::uint64_t>(drumFrames, drumExpected + SAMPLE_RATE / 2);
        auto drumPeak = drumOutput.begin() + drumSearchStart * CHANNELS;
        for (auto i = drumPeak; i < drumOutput.begin() + drumSearchEnd * CHANNELS; ++i) {
            if (std::abs(*i) > std::abs(*drumPeak)) {
                drumPeak = i;
            }
        }
        REQUIRE(std::abs(*drumPeak) > 0.3f);
        const auto drumPeakFrame = static_cast<std::uint64_t>((drumPeak - drumOutput.begin()) / CHANNELS);
        double drumMeasured = 0;
        REQUIRE(drumStream->position(static_cast<double>(drumPeakFrame), drumMeasured));
        const double drumTimingMs =
            (drumMeasured - drumState.drum / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::uint64_t drumLeft = drumPeakFrame;
        while (drumLeft > 0 &&
            std::abs(drumOutput[(drumLeft - 1) * CHANNELS]) > std::abs(*drumPeak) * 0.5f) {
            --drumLeft;
        }
        std::uint64_t drumRight = drumPeakFrame;
        while (drumRight + 1 < drumFrames &&
            std::abs(drumOutput[(drumRight + 1) * CHANNELS]) > std::abs(*drumPeak) * 0.5f) {
            ++drumRight;
        }
        std::uint64_t drumTail = drumPeakFrame;
        const auto drumTailEnd =
            std::min<std::uint64_t>(drumFrames, drumPeakFrame + SAMPLE_RATE * 8u);
        for (auto f = drumPeakFrame; f < drumTailEnd; ++f) {
            if (std::abs(drumOutput[f * CHANNELS]) > std::abs(*drumPeak) * 0.1f) {
                drumTail = f;
            }
        }
        const double drumWidthMs = (drumRight - drumLeft + 1) * 1000.0 / SAMPLE_RATE;
        const double drumTailMs = (drumTail - drumPeakFrame) * 1000.0 / SAMPLE_RATE;
        std::printf("drum speed %.2f: peak=%.3f width50=%.1fms tail10=%.0fms timing=%+.1fms\n",
            speed, std::abs(*drumPeak), drumWidthMs, drumTailMs, drumTimingMs);
        std::uint64_t drumOnset = drumPeakFrame;
        while (drumOnset > drumSearchStart &&
            std::abs(drumOutput[(drumOnset - 1) * CHANNELS]) > std::abs(*drumPeak) * 0.25f) {
            --drumOnset;
        }
        double drumOnsetMeasured = 0;
        REQUIRE(drumStream->position(static_cast<double>(drumOnset), drumOnsetMeasured));
        const double drumOnsetMs =
            (drumOnsetMeasured - drumState.drum / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::printf("drum onset speed %.2f: onset=%+.1fms\n", speed, drumOnsetMs);
        REQUIRE(std::abs(drumOnsetMs) < 10);
        REQUIRE(drumWidthMs < 60);
        REQUIRE(drumTailMs > 150 / speed && drumTailMs < 700 / speed);
        REQUIRE(std::abs(drumTimingMs) < 25);
        REQUIRE(drumStream->protectedTransients() > 0);
        drumStream.reset();
        Source impulseState;
        impulseState.length = SAMPLE_RATE * 6 / 5;
        impulseState.impulse = SAMPLE_RATE * 3 / 10;
        auto impulseBass = makeBass(impulseState);
        int impulseError;
        auto impulseStream = StretchTempoStream::create(impulseBass, 11, &impulseError);
        REQUIRE(impulseStream);
        impulseStream->setSpeed(speed, 1);
        const auto impulseOutput = render(impulseState);
        const auto impulseFrames = impulseOutput.size() / CHANNELS;
        auto impulsePeak = impulseOutput.begin();
        for (auto i = impulseOutput.begin(); i < impulseOutput.end(); ++i) {
            if (std::abs(*i) > std::abs(*impulsePeak)) {
                impulsePeak = i;
            }
        }
        REQUIRE(std::abs(*impulsePeak) > 0.15f);
        const auto impulsePeakFrame =
            static_cast<std::uint64_t>((impulsePeak - impulseOutput.begin()) / CHANNELS);
        double impulseMeasured = 0;
        REQUIRE(impulseStream->position(static_cast<double>(impulsePeakFrame), impulseMeasured));
        const double impulseTimingMs =
            (impulseMeasured - impulseState.impulse / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::uint64_t impulseLeft = impulsePeakFrame;
        while (impulseLeft > 0 &&
            std::abs(impulseOutput[(impulseLeft - 1) * CHANNELS]) > std::abs(*impulsePeak) * 0.1f) {
            --impulseLeft;
        }
        std::uint64_t impulseRight = impulsePeakFrame;
        while (impulseRight + 1 < impulseFrames &&
            std::abs(impulseOutput[(impulseRight + 1) * CHANNELS]) > std::abs(*impulsePeak) * 0.1f) {
            ++impulseRight;
        }
        const double impulseWidthMs = (impulseRight - impulseLeft + 1) * 1000.0 / SAMPLE_RATE;
        std::printf("impulse speed %.2f: peak=%.3f width10=%.1fms timing=%+.1fms\n",
            speed, std::abs(*impulsePeak), impulseWidthMs, impulseTimingMs);
        std::uint64_t impulseOnset = impulsePeakFrame;
        while (impulseOnset > 0 &&
            std::abs(impulseOutput[(impulseOnset - 1) * CHANNELS]) > std::abs(*impulsePeak) * 0.25f) {
            --impulseOnset;
        }
        double impulseOnsetMeasured = 0;
        REQUIRE(impulseStream->position(static_cast<double>(impulseOnset), impulseOnsetMeasured));
        const double impulseOnsetMs =
            (impulseOnsetMeasured - impulseState.impulse / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::printf("impulse onset speed %.2f: onset=%+.1fms\n", speed, impulseOnsetMs);
        REQUIRE(std::abs(impulseOnsetMs) < 5);
        REQUIRE(impulseWidthMs < 15);
        REQUIRE(std::abs(impulseTimingMs) < 20);
        REQUIRE(impulseStream->protectedTransients() > 0);
        impulseStream.reset();
    }
}

void testTransientSelectivity() {
    Source state;
    state.length = SAMPLE_RATE * 4;
    state.punch = SAMPLE_RATE;
    auto bass = makeBass(state);
    int errorCode;
    auto stream = StretchTempoStream::create(bass, 11, &errorCode);
    REQUIRE(stream);
    stream->setSpeed(0.5f, 1);
    const auto output = render(state);
    const auto frames = output.size() / CHANNELS;
    REQUIRE(std::abs(static_cast<double>(frames) - state.length / 0.5) < 22);
    float peak = 0;
    for (const auto sample : output) {
        REQUIRE(std::isfinite(sample));
        peak = std::max(peak, std::abs(sample));
    }
    REQUIRE(peak > 0.4f);
    REQUIRE(peak < 1.5f);
    std::printf("punch speed 0.50: protected=%d peak=%.3f\n", stream->protectedTransients(), peak);
    REQUIRE(stream->protectedTransients() == 0);
    stream.reset();
    Source drumState;
    drumState.length = SAMPLE_RATE * 5 / 2;
    drumState.drum = SAMPLE_RATE;
    auto drumBass = makeBass(drumState);
    int drumError;
    auto drumStream = StretchTempoStream::create(drumBass, 11, &drumError);
    REQUIRE(drumStream);
    drumStream->setSpeed(0.5f, 1);
    const auto drumOutput = render(drumState);
    REQUIRE(!drumOutput.empty());
    std::printf("drum speed 0.50: protected=%d\n", drumStream->protectedTransients());
    REQUIRE(drumStream->protectedTransients() > 0);
    drumStream.reset();
}

void testNoiseStability() {
    Source state;
    state.length = SAMPLE_RATE * 4;
    state.noise = true;
    auto bass = makeBass(state);
    int errorCode;
    auto stream = StretchTempoStream::create(bass, 11, &errorCode);
    REQUIRE(stream);
    stream->setSpeed(0.5f, 1);
    const auto output = render(state);
    const auto frames = output.size() / CHANNELS;
    REQUIRE(std::abs(static_cast<double>(frames) - state.length / 0.5) < 22);
    float peak = 0;
    double sum = 0;
    for (const auto sample : output) {
        REQUIRE(std::isfinite(sample));
        peak = std::max(peak, std::abs(sample));
        sum += sample * sample;
    }
    const double rms = std::sqrt(sum / output.size());
    REQUIRE(peak < 2.0f);
    REQUIRE(rms > 0.05 && rms < 0.6);
    REQUIRE(stream->protectedTransients() == 0);
    std::printf("noise speed 0.50: protected=%d peak=%.3f rms=%.3f\n",
        stream->protectedTransients(), peak, rms);
    stream.reset();
}

void testDenseMix() {
    Source state;
    state.length = SAMPLE_RATE * 4;
    state.dense = true;
    auto bass = makeBass(state);
    int errorCode;
    auto stream = StretchTempoStream::create(bass, 11, &errorCode);
    REQUIRE(stream);
    stream->setSpeed(0.5f, 1);
    const auto output = render(state);
    const auto frames = output.size() / CHANNELS;
    REQUIRE(std::abs(static_cast<double>(frames) - state.length / 0.5) < 22);
    float peak = 0;
    for (const auto sample : output) {
        REQUIRE(std::isfinite(sample));
        peak = std::max(peak, std::abs(sample));
    }
    REQUIRE(peak < 3.0f);
    std::printf("dense speed 0.50: protected=%d peak=%.3f\n", stream->protectedTransients(), peak);
    REQUIRE(stream->protectedTransients() > 0);
    stream.reset();
}

void testHalfSpeedPercussion() {
    for (int kind : {0, 1, 2}) {
        const bool snare = kind == 1;
        Source state;
        state.pcm.resize(state.length * CHANNELS);
        std::uint32_t seed = 6789;
        float previous = 0;
        for (std::uint64_t frame = 0; frame < state.length; ++frame) {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            const float noise = static_cast<float>(static_cast<std::int32_t>(seed)) / 2147483648.0f;
            const auto phase = frame % (SAMPLE_RATE / 4);
            const float decay = std::exp(-static_cast<float>(phase) / (snare ? 2205.0f : 441.0f));
            float value = 0.25f * (noise - previous) * decay;
            previous = noise;
            if (snare) {
                value += 0.25f * static_cast<float>(std::sin(2 * PI * 180 * phase / SAMPLE_RATE)) * decay;
            }
            if (kind == 2) {
                const double chord = std::sin(2 * PI * 110 * frame / SAMPLE_RATE) +
                    std::sin(2 * PI * 165 * frame / SAMPLE_RATE) +
                    std::sin(2 * PI * 220 * frame / SAMPLE_RATE);
                value += 0.2f * static_cast<float>(std::tanh(2 * chord));
            }
            state.pcm[frame * CHANNELS] = value;
            state.pcm[frame * CHANNELS + 1] = kind == 2 ? -value : value;
        }
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(0.5f, 1);
        const auto output = render(state);
        auto sampleAt = [&](int frame) {
            const double sample = output[frame * CHANNELS];
            if (kind == 2) {
                return sample - 2.0 * output[(frame - 1) * CHANNELS] +
                    output[(frame - 2) * CHANNELS];
            }
            return sample;
        };
        double preEcho = 0;
        double attack = 0;
        double width = 0;
        constexpr int FIRST_HIT = 2;
        constexpr int LAST_HIT = 14;
        for (int hit = FIRST_HIT; hit < LAST_HIT; ++hit) {
            const int nominal = hit * (SAMPLE_RATE / 4) * 2;
            const double expected = static_cast<double>(hit * (SAMPLE_RATE / 4)) / SAMPLE_RATE;
            const int searchBegin = std::max(2, nominal - SAMPLE_RATE / 2);
            const int searchEnd = std::min<int>(output.size() / CHANNELS - 1, nominal + SAMPLE_RATE / 2);
            int center = nominal;
            for (int frame = searchBegin; frame < searchEnd; ++frame) {
                double mappedTime = 0;
                if (stream->position(static_cast<double>(frame), mappedTime) &&
                    mappedTime >= expected) {
                    center = frame;
                    break;
                }
            }
            double mapped = 0;
            REQUIRE(stream->position(static_cast<double>(center), mapped));
            REQUIRE(std::abs(mapped - expected) < 0.015);
            const int begin = center - SAMPLE_RATE / 10;
            const int end = center + SAMPLE_RATE / 5;
            double energy = 0;
            double before = 0;
            double firstTenMs = 0;
            for (int frame = begin; frame < end; ++frame) {
                const double sample = sampleAt(frame);
                const double power = sample * sample;
                energy += power;
                if (frame < center) {
                    before += power;
                } else if (frame < center + SAMPLE_RATE / 100) {
                    firstTenMs += power;
                }
            }
            double cumulative = 0;
            int low = begin;
            int high = begin;
            for (int frame = begin; frame < end; ++frame) {
                const double sample = sampleAt(frame);
                cumulative += sample * sample;
                if (cumulative < energy * 0.05) {
                    low = frame;
                }
                if (cumulative < energy * 0.95) {
                    high = frame;
                }
            }
            preEcho += before / energy;
            attack += firstTenMs / energy;
            width += (high - low) * 1000.0 / SAMPLE_RATE;
        }
        preEcho /= LAST_HIT - FIRST_HIT;
        attack /= LAST_HIT - FIRST_HIT;
        width /= LAST_HIT - FIRST_HIT;
        std::printf("%s speed 0.50: pre-echo=%.3f first10ms=%.3f width90=%.1fms\n",
            snare ? "snare" : (kind == 2 ? "hi-hat in chord" : "hi-hat"), preEcho, attack, width);
        REQUIRE(preEcho < 0.12);
        if (!snare) {
            REQUIRE(attack > 0.5);
            REQUIRE(width < 30);
        }
        for (std::size_t frame = 0; frame < output.size() / CHANNELS; ++frame) {
            const float expected = kind == 2 ? -output[frame * CHANNELS] : output[frame * CHANNELS];
            REQUIRE(std::abs(output[frame * CHANNELS + 1] - expected) < 0.002f);
        }
        state.frame = 0;
        REQUIRE(stream->flush());
        REQUIRE(render(state, 103) == output);
    }
}

void testFormatsAndStereo(bool adaptive = false) {
    for (std::uint32_t rate : {8000u, 44100u, 48000u, 96000u, 192000u}) {
        for (std::uint32_t channels : {1u, 2u, 8u}) {
            Source state;
            state.sampleRate = rate;
            state.channels = channels;
            state.length = rate / 4;
            state.chunk = 73;
            state.pcm.resize(state.length * channels);
            for (std::uint64_t frame = 0; frame < state.length; ++frame) {
                const float sample = static_cast<float>(0.3 * std::sin(2 * PI * 440 * frame / rate));
                for (std::uint32_t channel = 0; channel < channels; ++channel) {
                    state.pcm[frame * channels + channel] = channel == 0 ? sample :
                        (channel == 1 ? -sample : 0);
                }
            }
            auto bass = makeBass(state);
            int errorCode;
            StretchConfig config;
            config.adaptiveSidelobeLock = adaptive;
            auto stream = StretchTempoStream::create(bass, 11, &errorCode, config);
            REQUIRE(stream);
            REQUIRE(stream->latencyFrames() - 2 * StretchTempoStream::BLOCK_FRAMES >= 0.02 * rate);
            stream->setSpeed(0.5f, 1);
            const auto output = render(state, 127);
            REQUIRE(output.size() / channels == state.length * 2);
            double maxError = 0;
            for (std::size_t frame = 0; frame < output.size() / channels; ++frame) {
                REQUIRE(std::isfinite(output[frame * channels]));
                for (std::uint32_t channel = 1; channel < channels; ++channel) {
                    const float expected = channel == 1 ? -output[frame * channels] : 0;
                    const double error = std::abs(output[frame * channels + channel] - expected);
                    maxError = std::max(maxError, error);
                    REQUIRE(error < 0.002f);
                }
            }
            std::printf("stereo rate %u channels %u speed 0.50: max-error=%.9f\n",
                rate, channels, maxError);
        }
    }
}

void testShortClipsAndParameterExtremes() {
    for (int length : {1, 127, 256, 4097}) {
        for (float speed : {0.05f, 0.5f, 1.0f, 2.0f, 51.0f}) {
            for (float pitch : {1.0f / 32, 1.0f, 32.0f}) {
                Source state;
                state.length = length;
                state.impulse = length - 1;
                auto bass = makeBass(state);
                int errorCode;
                auto stream = StretchTempoStream::create(bass, 11, &errorCode);
                REQUIRE(stream);
                stream->setSpeed(speed, pitch);
                const auto output = render(state, 73);
                REQUIRE(std::abs(static_cast<double>(output.size() / CHANNELS) - length / speed) < 22);
                for (float sample : output) {
                    REQUIRE(std::isfinite(sample));
                }
                if (speed == 1 && pitch == 1) {
                    REQUIRE(output.size() == state.length * CHANNELS);
                    REQUIRE(output[(length - 1) * CHANNELS] > 0.8f);
                }
            }
        }
    }
}

void testVerySlowHat() {
    for (float speed : {0.1f}) {
        Source state;
        state.length = SAMPLE_RATE * 2;
        const int hat = SAMPLE_RATE;
        state.pcm.resize(state.length * CHANNELS);
        std::uint32_t seed = 6789;
        float previous = 0;
        for (std::uint64_t frame = 0; frame < state.length; ++frame) {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            const float noise = static_cast<float>(static_cast<std::int32_t>(seed)) / 2147483648.0f;
            const auto phase = frame > static_cast<std::uint64_t>(hat) ? frame - hat : 0;
            const float decay = phase > 0 ? std::exp(-static_cast<float>(phase) / 441.0f) : 0.0f;
            const float value = 0.25f * (noise - previous) * decay;
            previous = noise;
            state.pcm[frame * CHANNELS] = value;
            state.pcm[frame * CHANNELS + 1] = value;
        }
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(speed, 1);
        const auto output = render(state);
        const auto frames = output.size() / CHANNELS;
        const auto expected = static_cast<std::uint64_t>(hat / static_cast<double>(speed));
        const auto searchStart = expected > SAMPLE_RATE ? expected - SAMPLE_RATE : 0;
        const auto searchEnd = std::min<std::uint64_t>(frames, expected + SAMPLE_RATE);
        auto peak = output.begin() + searchStart * CHANNELS;
        for (auto i = peak; i < output.begin() + searchEnd * CHANNELS; ++i) {
            if (std::abs(*i) > std::abs(*peak)) {
                peak = i;
            }
        }
        REQUIRE(std::abs(*peak) > 0.05f);
        const auto peakFrame = static_cast<std::uint64_t>((peak - output.begin()) / CHANNELS);
        std::uint64_t onset = peakFrame;
        while (onset > searchStart &&
            std::abs(output[(onset - 1) * CHANNELS]) > std::abs(*peak) * 0.25f) {
            --onset;
        }
        double onsetMeasured = 0;
        REQUIRE(stream->position(static_cast<double>(onset), onsetMeasured));
        const double onsetMs =
            (onsetMeasured - hat / static_cast<double>(SAMPLE_RATE)) * 1000.0;
        std::printf("hat onset speed %.2f: onset=%+.1fms\n", speed, onsetMs);
        REQUIRE(std::abs(onsetMs) < 12);
        REQUIRE(stream->protectedTransients() > 0);
        stream.reset();
    }
}

void testIdealPositionHidesTransientStride() {
    {
        Source state;
        state.length = SAMPLE_RATE * 2;
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(0.35f, 1);
        const auto output = render(state);
        const auto delivered = output.size() / CHANNELS;
        REQUIRE(stream->protectedTransients() == 0);
        for (std::uint64_t frame = 0; frame < delivered; frame += StretchTempoStream::BLOCK_FRAMES) {
            double actual = 0;
            double ideal = 0;
            REQUIRE(stream->position(static_cast<double>(frame), actual));
            REQUIRE(stream->idealPosition(static_cast<double>(frame), ideal));
            REQUIRE(actual == ideal);
        }
        stream.reset();
    }
    {
        Source state;
        state.length = SAMPLE_RATE * 3;
        state.drum = SAMPLE_RATE;
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(0.35f, 1);
        const auto output = render(state);
        const auto delivered = output.size() / CHANNELS;
        REQUIRE(stream->protectedTransients() > 0);
        const double nominal = 0.35 * StretchTempoStream::BLOCK_FRAMES / SAMPLE_RATE;
        const double frameSeconds = 1.0 / SAMPLE_RATE;
        double maxGap = 0;
        bool sawStride = false;
        bool havePrevious = false;
        double previousActual = 0;
        double previousIdeal = 0;
        for (std::uint64_t frame = 0; frame + StretchTempoStream::BLOCK_FRAMES <= delivered; frame += StretchTempoStream::BLOCK_FRAMES) {
            double actual = 0;
            double ideal = 0;
            REQUIRE(stream->position(static_cast<double>(frame), actual));
            REQUIRE(stream->idealPosition(static_cast<double>(frame), ideal));
            const double gap = std::abs(actual - ideal);
            if (gap > maxGap) {
                maxGap = gap;
            }
            if (havePrevious) {
                REQUIRE(ideal >= previousIdeal);
                REQUIRE(std::abs((ideal - previousIdeal) - nominal) <= 1.5 * frameSeconds);
                if (std::abs((actual - previousActual) - nominal) > 0.002) {
                    sawStride = true;
                }
            }
            havePrevious = true;
            previousActual = actual;
            previousIdeal = ideal;
        }
        REQUIRE(havePrevious);
        REQUIRE(maxGap > 0.002);
        REQUIRE(sawStride);
        stream.reset();
    }
}

void testRecordedTexture() {
    std::vector<float> first;
    for (const float speed : {1.0f, 0.5f, 0.35f}) {
        Source state;
        state.length = SAMPLE_RATE * 2;
        state.noise = true;
        auto bass = makeBass(state);
        int errorCode;
        auto stream = StretchTempoStream::create(bass, 11, &errorCode);
        REQUIRE(stream);
        stream->setSpeed(speed, 1);
        const std::vector<float> output = render(state);
        for (float value : output) {
            REQUIRE(std::isfinite(value));
        }
        if (speed == 0.5f) {
            first = output;
            Source repeat;
            repeat.length = SAMPLE_RATE * 2;
            repeat.noise = true;
            auto repeatBass = makeBass(repeat);
            auto repeatStream = StretchTempoStream::create(repeatBass, 11, &errorCode);
            REQUIRE(repeatStream);
            repeatStream->setSpeed(speed, 1);
            REQUIRE(render(repeat) == output);
        }
        if (speed == 0.35f) {
            REQUIRE(output.size() > first.size());
        }
    }
}

void runStretchTempoStreamTests() {
    testAdaptiveSidelobeLock();
    testAdaptiveSidelobeTransitions();
    runTextureGrainsTests();
    testGainIndependentPhase();
    testRatesPitchAndTail();
    testSlowPitchStability();
    testSlowChordStability();
    testSlowHighToneStability();
    testVibratoTransientSelectivity();
    testAlignmentAndReset();
    testPositionHistoryAndTransitions();
    testConcurrentPositionQueries();
    testTransientStepResponse();
    testTransientSharpness();
    testDrumTransient();
    testVerySlowTransients();
    testVerySlowHat();
    testTransientSelectivity();
    testNoiseStability();
    testRecordedTexture();
    testDenseMix();
    testHalfSpeedPercussion();
    testFormatsAndStereo();
    testShortClipsAndParameterExtremes();
    testIdealPositionHidesTransientStride();
}


