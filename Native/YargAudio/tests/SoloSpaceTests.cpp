#include "dsp/SoloSpace.h"
#include "Test.h"

#include <cmath>
#include <cstdint>
#include <vector>

namespace {

constexpr double PI = 3.14159265358979323846;
constexpr int SAMPLE_RATE = 48000;
constexpr double SHORTEST_HELD_SECONDS = 0.24;
constexpr double HELD_STEP_SECONDS = 0.05;
constexpr double MUTE_SECONDS = 0.015;
constexpr double NOISE_FLOOR = 3e-4;
constexpr double REVERB_LENGTH = 1.5;

struct Solo {
    std::vector<float> region;
    std::vector<double> picks;
};

void addRoom(std::vector<float>& samples, double wet, double reverbSeconds) {
    const auto length = static_cast<std::size_t>(REVERB_LENGTH * reverbSeconds * SAMPLE_RATE);
    std::vector<float> response(length);
    std::uint32_t noise = 0x12345678u;
    double energy = 0;
    for (std::size_t tap = 0; tap < length; ++tap) {
        noise ^= noise << 13;
        noise ^= noise >> 17;
        noise ^= noise << 5;
        const double decay = std::exp(-6.9 * static_cast<double>(tap) / SAMPLE_RATE / reverbSeconds);
        response[tap] = static_cast<float>((static_cast<double>(noise) / 2147483648.0 - 1.0) * decay);
        energy += response[tap] * response[tap];
    }
    const double scale = wet / std::sqrt(energy);
    const auto dry = samples;
    for (std::size_t frame = 0; frame < dry.size(); ++frame) {
        if (dry[frame] == 0.0f) continue;
        for (std::size_t tap = 0; tap < length && frame + tap < samples.size(); ++tap) {
            samples[frame + tap] += static_cast<float>(scale * dry[frame] * response[tap]);
        }
    }
}

Solo solo(double echoSeconds, double echoLevel, double wet, double reverbSeconds, int notes = 8) {
    Solo built;
    double time = 0.2;
    for (int note = 0; note < notes; ++note) {
        built.picks.push_back(time);
        time += 0.75 + 0.1 * (note % 3);
    }
    const auto frames = static_cast<std::size_t>((time + 0.5) * SAMPLE_RATE);
    std::vector<float> dry(frames, 0.0f);
    for (std::size_t note = 0; note < built.picks.size(); ++note) {
        const double hz = 220 * std::pow(2.0, static_cast<double>(note % 5) / 5);
        const auto first = static_cast<std::size_t>(built.picks[note] * SAMPLE_RATE);
        const double held = SHORTEST_HELD_SECONDS + HELD_STEP_SECONDS * static_cast<double>(note % 4);
        const auto length = static_cast<std::size_t>((held + MUTE_SECONDS) * SAMPLE_RATE);
        for (std::size_t frame = 0; frame < length; ++frame) {
            const double seconds = static_cast<double>(frame) / SAMPLE_RATE;
            const double mute = seconds < held ? 1.0 : 1.0 - (seconds - held) / MUTE_SECONDS;
            double value = 0;
            for (int harmonic = 1; harmonic <= 8; ++harmonic) {
                value += std::sin(2 * PI * hz * harmonic * seconds) / harmonic;
            }
            dry[first + frame] += static_cast<float>(0.1 * std::exp(-3 * seconds) * mute * value);
        }
    }

    const auto delay = static_cast<std::size_t>(echoSeconds * SAMPLE_RATE);
    built.region = dry;
    for (std::size_t frame = delay; delay > 0 && frame < frames; ++frame) {
        built.region[frame] += static_cast<float>(echoLevel * dry[frame - delay]);
    }
    if (wet > 0) addRoom(built.region, wet, reverbSeconds);
    std::uint32_t noise = 0x9E3779B9u;
    for (float& sample : built.region) {
        noise ^= noise << 13;
        noise ^= noise >> 17;
        noise ^= noise << 5;
        sample += static_cast<float>(NOISE_FLOOR * (static_cast<double>(noise) / 2147483648.0 - 1.0));
    }
    return built;
}

yarg_solo_space defaults() {
    yarg_solo_space space{};
    space.size = sizeof(space);
    space.echo_seconds = 0.37f;
    space.echo_level = 0.35f;
    space.reverb_wet = 0.47f;
    space.reverb_room = 0.75f;
    return space;
}

int measure(const Solo& built, yarg_solo_space& space) {
    return yarg::audio::soloSpaceMatch(SAMPLE_RATE, built.region.data(), built.region.size(), built.picks.data(),
        built.picks.size(), &space);
}

void dryRegionHasNoEchoOrReverb() {
    auto space = defaults();
    REQUIRE(measure(solo(0, 0, 0, 0), space) == YARG_AUDIO_OK);
    REQUIRE(space.echo_level == 0.0f);
    REQUIRE(space.reverb_wet < 0.15f);
}

void echoIsFound() {
    auto during = defaults();
    REQUIRE(measure(solo(0.22, 0.4, 0, 0), during) == YARG_AUDIO_OK);
    REQUIRE(std::abs(during.echo_seconds - 0.22f) < 0.021f);
    REQUIRE(during.echo_level > 0.1f && during.echo_level < 0.8f);

    auto after = defaults();
    REQUIRE(measure(solo(0.45, 0.4, 0, 0), after) == YARG_AUDIO_OK);
    REQUIRE(std::abs(after.echo_seconds - 0.45f) < 0.021f);
    REQUIRE(after.echo_level > 0.2f && after.echo_level < 0.8f);
}

void reverbIsFound() {
    auto small = defaults();
    auto large = defaults();
    REQUIRE(measure(solo(0, 0, 0.5, 0.4), small) == YARG_AUDIO_OK);
    REQUIRE(measure(solo(0, 0, 0.5, 1.5), large) == YARG_AUDIO_OK);
    REQUIRE(small.reverb_room < large.reverb_room);
    REQUIRE(small.reverb_room < 0.25f);
    REQUIRE(large.reverb_room > 0.35f && large.reverb_room < 0.85f);
    REQUIRE(large.reverb_wet > 0.2f);
    REQUIRE(small.echo_level == 0.0f && large.echo_level == 0.0f);
}

void tooFewGapsLeaveDefaults() {
    auto space = defaults();
    REQUIRE(measure(solo(0, 0, 0, 0, 2), space) == YARG_AUDIO_ERROR_INVALID_STATE);
    REQUIRE(space.echo_level == 0.35f && space.reverb_wet == 0.47f && space.reverb_room == 0.75f);
}

}

void runSoloSpaceTests() {
    dryRegionHasNoEchoOrReverb();
    echoIsFound();
    reverbIsFound();
    tooFewGapsLeaveDefaults();
}
