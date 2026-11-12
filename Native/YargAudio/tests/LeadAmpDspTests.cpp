#include "dsp/LeadAmpDsp.h"
#include "Test.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <iterator>
#include <vector>

using yarg::audio::LeadAmp;
using Tone = std::array<float, LeadAmp::TONE_BANDS>;
using yarg::audio::Cabinet;

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

yarg_lead_amp_tone toneOf(float drive, const Tone& bands, float wahStartHz = 0.0f, float wahEndHz = 0.0f) {
    auto tone = LeadAmp::defaultTone();
    tone.drive = drive;
    std::copy(bands.begin(), bands.end(), tone.bands_db);
    tone.wah_start_hz = wahStartHz;
    tone.wah_end_hz = wahEndHz;
    return tone;
}

std::vector<float> run(std::vector<float> samples, float gain, const std::vector<float>& cabinet = {},
    float drive = 1.0f, const Tone& tone = {}, float wahStartHz = 0.0f, float wahEndHz = 0.0f) {
    LeadAmp amp(CHANNELS, SAMPLE_RATE, cabinet.data(), cabinet.size(), SAMPLE_RATE);
    amp.setTone(toneOf(drive, tone, wahStartHz, wahEndHz));
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

constexpr double STRING_DAMPING = 2.0;

std::vector<float> pluck(double frequency, int frames, double damping = STRING_DAMPING) {
    std::vector<float> samples(static_cast<std::size_t>(frames));
    for (int frame = 0; frame < frames; ++frame) {
        const double time = static_cast<double>(frame) / SAMPLE_RATE;
        double value = 0;
        for (int harmonic = 1; harmonic <= 12; ++harmonic) {
            value += std::sin(2 * PI * frequency * harmonic * time) / harmonic
                * std::exp(-damping * (harmonic - 1) * time);
        }
        samples[frame] = static_cast<float>(0.1 * std::exp(-4 * time) * value);
    }
    return samples;
}

std::vector<float> monoRender(const std::vector<float>& input, float drive, const Tone& tone,
    float wahStartHz = 0.0f, float wahEndHz = 0.0f, const std::vector<float>* cabinet = nullptr) {
    const Cabinet source{cabinet ? cabinet->data() : nullptr, cabinet ? cabinet->size() : 0, SAMPLE_RATE};
    LeadAmp amp(1, SAMPLE_RATE, &source, cabinet ? 1 : 0);
    auto setting = toneOf(drive, tone, wahStartHz, wahEndHz);
    setting.echo_level = 0.0f;
    amp.setTone(setting);
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
    float wahStartHz;
    float wahEndHz;
    float levelGain;
    std::uint32_t cabinet;
};

void limit(std::vector<float>& samples) {
    double peak = 0;
    for (float sample : samples) peak = std::max(peak, static_cast<double>(std::abs(sample)));
    const double ceiling = 0.25 * peak;
    double envelope = 0, gain = 1;
    for (float& sample : samples) {
        envelope = std::max(static_cast<double>(std::abs(sample)), envelope * 0.9995);
        const double wanted = envelope > ceiling ? std::pow(ceiling / envelope, 0.8) : 1.0;
        gain += (wanted - gain) * (wanted < gain ? 0.3 : 0.0005);
        sample = static_cast<float>(sample * gain);
    }
}

Matched match(float drive, const Tone& tone, int noteCount, float wahStartHz = 0.0f, float wahEndHz = 0.0f,
    double rumble = 0.0, const std::vector<std::vector<float>>& cabinets = {}, int stemCabinet = 0,
    int noteFrames = SAMPLE_RATE * 2 / 5, bool limited = false) {
    std::vector<float> stem, di;
    std::vector<std::uint32_t> frames;
    for (int note = 0; note < noteCount; ++note) {
        const auto played = pluck(196 * std::pow(2.0, note / 4.0), noteFrames);
        auto heard = monoRender(played, drive, tone, wahStartHz, wahEndHz,
            cabinets.empty() ? nullptr : &cabinets[stemCabinet]);
        for (std::size_t frame = 0; frame < heard.size(); ++frame) {
            heard[frame] += static_cast<float>(rumble * std::sin(2 * PI * 55 * frame / SAMPLE_RATE));
        }
        if (limited) limit(heard);
        di.insert(di.end(), played.begin(), played.end());
        stem.insert(stem.end(), heard.begin(), heard.end());
        frames.push_back(static_cast<std::uint32_t>(played.size()));
    }
    std::vector<Cabinet> sources;
    for (const auto& cabinet : cabinets) {
        sources.push_back({cabinet.data(), cabinet.size(), SAMPLE_RATE});
    }
    auto fitted = LeadAmp::defaultTone();
    Matched matched{};
    matched.result = yarg::audio::leadAmpMatch(SAMPLE_RATE, sources.data(), sources.size(), stem.data(), di.data(),
        frames.data(), frames.size(), &fitted);
    matched.drive = fitted.drive;
    std::copy(std::begin(fitted.bands_db), std::end(fitted.bands_db), matched.tone.begin());
    matched.wahStartHz = fitted.wah_start_hz;
    matched.wahEndHz = fitted.wah_end_hz;
    matched.levelGain = fitted.level_gain;
    matched.cabinet = fitted.cabinet;
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

void toneBandBoostsItsFrequency() {
    Tone tone{};
    tone[12] = 12.0f;
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
    const Tone flat{};
    REQUIRE(LeadAmp::validTone(toneOf(1.0f, flat)));
    REQUIRE(!LeadAmp::validTone(toneOf(LeadAmp::MAX_DRIVE * 2, flat)));
    REQUIRE(LeadAmp::validTone(toneOf(1.0f, flat, 500.0f, 1500.0f)));
    REQUIRE(!LeadAmp::validTone(toneOf(1.0f, flat, 1000.0f, 0.0f)));
    REQUIRE(!LeadAmp::validTone(toneOf(1.0f, flat, 200.0f, 1000.0f)));
    Tone loud{};
    loud[0] = LeadAmp::MAX_TONE_DB + 1;
    REQUIRE(!LeadAmp::validTone(toneOf(1.0f, loud)));
    auto echo = toneOf(1.0f, flat);
    echo.echo_seconds = LeadAmp::MAX_ECHO_SECONDS * 2;
    REQUIRE(!LeadAmp::validTone(echo));
    echo = toneOf(1.0f, flat);
    echo.echo_level = 2.0f;
    REQUIRE(!LeadAmp::validTone(echo));
    auto small = toneOf(1.0f, flat);
    small.size = sizeof(yarg_lead_amp_tone) - 4;
    REQUIRE(!LeadAmp::validTone(small));
}

std::vector<float> resonantCabinet(double hz) {
    std::vector<float> cabinet(512);
    for (std::size_t tap = 0; tap < cabinet.size(); ++tap) {
        cabinet[tap] = static_cast<float>(std::exp(-0.02 * tap) * std::cos(2 * PI * hz * tap / SAMPLE_RATE));
    }
    return cabinet;
}

void toneSwitchesCabinet() {
    const auto dark = resonantCabinet(500);
    const auto bright = resonantCabinet(2500);
    const std::array<Cabinet, 2> cabinets{{{dark.data(), dark.size(), SAMPLE_RATE},
        {bright.data(), bright.size(), SAMPLE_RATE}}};
    const auto input = sine(330, 0.1, SAMPLE_RATE / 2);
    auto viaDark = input, viaBright = input, single = input;
    LeadAmp both(CHANNELS, SAMPLE_RATE, cabinets.data(), cabinets.size());
    both.process(viaDark.data(), viaDark.size() / CHANNELS, 1.0f);
    auto tone = LeadAmp::defaultTone();
    tone.cabinet = 1;
    LeadAmp switched(CHANNELS, SAMPLE_RATE, cabinets.data(), cabinets.size());
    switched.setTone(tone);
    switched.process(viaBright.data(), viaBright.size() / CHANNELS, 1.0f);
    REQUIRE(both.cabinetCount() == 2);
    REQUIRE(viaDark == run(input, 1.0f, dark));
    const int first = SAMPLE_RATE / 4;
    const int frames = 8192;
    REQUIRE(tonePower(viaBright, first, frames, 2310) / tonePower(viaBright, first, frames, 330)
        > 4 * tonePower(viaDark, first, frames, 2310) / tonePower(viaDark, first, frames, 330));
}

void echoFollowsTheTone() {
    auto input = sine(330, 0.1, SAMPLE_RATE);
    std::fill(input.begin() + SAMPLE_RATE / 20 * CHANNELS, input.end(), 0.0f);
    auto tone = LeadAmp::defaultTone();
    tone.echo_seconds = 0.2f;
    LeadAmp amp(CHANNELS, SAMPLE_RATE);
    amp.setTone(tone);
    auto output = input;
    amp.process(output.data(), output.size() / CHANNELS, 1.0f);
    const int frames = SAMPLE_RATE / 50;
    const double echoed = rms(output, 0, SAMPLE_RATE / 5, frames);
    REQUIRE(echoed > 10 * rms(output, 0, SAMPLE_RATE * 3 / 20, frames));

    tone.echo_level = 0.0f;
    LeadAmp dry(CHANNELS, SAMPLE_RATE);
    dry.setTone(tone);
    output = input;
    dry.process(output.data(), output.size() / CHANNELS, 1.0f);
    REQUIRE(rms(output, 0, SAMPLE_RATE / 5, frames) < 0.1 * echoed);
}

void matchPicksTheCabinet() {
    const std::vector<std::vector<float>> cabinets{resonantCabinet(500), resonantCabinet(2500)};
    for (int stemCabinet = 0; stemCabinet < 2; ++stemCabinet) {
        const auto matched = match(1.0f, {}, 8, 0.0f, 0.0f, 0.0, cabinets, stemCabinet);
        REQUIRE(matched.result == YARG_AUDIO_OK);
        REQUIRE(matched.cabinet == static_cast<std::uint32_t>(stemCabinet));
    }
}

std::vector<float> stereoPluck(double frequency, int frames, int secondPickFrame = -1) {
    const auto mono = pluck(frequency, frames, 0.0);
    std::vector<float> samples(static_cast<std::size_t>(frames) * CHANNELS);
    for (int frame = 0; frame < frames; ++frame) {
        const float value = secondPickFrame >= 0 && frame >= secondPickFrame
            ? mono[frame - secondPickFrame]
            : mono[frame];
        samples[frame * CHANNELS] = value;
        samples[frame * CHANNELS + 1] = value;
    }
    return samples;
}

double brightness(const std::vector<float>& samples, int firstFrame) {
    const int frames = SAMPLE_RATE / 20;
    return tonePower(samples, firstFrame, frames, 1980) / tonePower(samples, firstFrame, frames, 495);
}

void wahSweepsDownAfterThePick() {
    const auto input = stereoPluck(165, SAMPLE_RATE / 2);
    const auto plain = run(input, 1.0f);
    const auto wah = run(input, 1.0f, {}, 1.0f, {}, 2000.0f, 450.0f);
    const int early = SAMPLE_RATE / 100;
    const int late = SAMPLE_RATE / 4;
    REQUIRE(brightness(wah, early) / brightness(wah, late) > 4 * brightness(plain, early) / brightness(plain, late));
}

void wahRestartsOnTheNextPick() {
    const int secondPick = SAMPLE_RATE / 2;
    const auto input = stereoPluck(165, SAMPLE_RATE, secondPick);
    const auto wah = run(input, 1.0f, {}, 1.0f, {}, 2000.0f, 450.0f);
    REQUIRE(brightness(wah, secondPick + SAMPLE_RATE / 100) > 4 * brightness(wah, secondPick - SAMPLE_RATE / 10));
}

void matchKeepsTheDefaultTone() {
    const auto matched = match(1.0f, {}, 8);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    REQUIRE(std::abs(std::log2(matched.drive)) < 0.3);
    for (float band : matched.tone) {
        REQUIRE(std::abs(band) < 2.0f);
    }
    REQUIRE(std::abs(matched.levelGain - 1.0f) < 0.1f);
    REQUIRE(matched.wahStartHz == 0.0f && matched.wahEndHz == 0.0f);
}

void matchFindsDriveAndTone() {
    Tone tone{};
    tone[6] = -6.0f;
    tone[15] = 6.0f;
    const auto matched = match(3.0f, tone, 8);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    REQUIRE(std::abs(std::log2(matched.drive / 3.0f)) < 0.6);
    REQUIRE(matched.tone[15] - matched.tone[6] > 8.0f);
    REQUIRE(matched.wahStartHz == 0.0f && matched.wahEndHz == 0.0f);
    REQUIRE(std::isfinite(matched.levelGain) && matched.levelGain > 0.0f);
}

void matchFindsWah() {
    const auto matched = match(2.0f, {}, 8, 1800.0f, 500.0f);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    REQUIRE(std::abs(std::log2(matched.wahStartHz / 1800.0f)) < 0.7);
    REQUIRE(std::abs(std::log2(matched.wahEndHz / 500.0f)) < 0.7);
    REQUIRE(std::abs(std::log2(matched.drive / 2.0f)) < 0.6);
}

void matchIgnoresLowBleed() {
    const auto matched = match(1.0f, {}, 8, 0.0f, 0.0f, 0.3);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    for (float band : matched.tone) {
        REQUIRE(std::abs(band) < 3.0f);
    }
}

void matchWorksOnShortNotes() {
    Tone tone{};
    tone[6] = -6.0f;
    tone[15] = 6.0f;
    const auto matched = match(3.0f, tone, 8, 0.0f, 0.0f, 0.0, {}, 0, SAMPLE_RATE * 12 / 100);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    REQUIRE(std::abs(std::log2(matched.drive / 3.0f)) < 1.0);
    REQUIRE(matched.tone[15] - matched.tone[6] > 6.0f);

    const auto wah = match(3.0f, {}, 8, 1800.0f, 500.0f, 0.0, {}, 0, SAMPLE_RATE * 12 / 100);
    REQUIRE(wah.result == YARG_AUDIO_OK);
    REQUIRE(wah.wahStartHz == 0.0f && wah.wahEndHz == 0.0f);
}

void requireGentleTone(const Tone& tone) {
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) {
        REQUIRE(tone[band] <= 6.0f + 1e-4f);
        if (band > 0) REQUIRE(std::abs(tone[band] - tone[band - 1]) <= 6.0f + 1e-4f);
    }
}

double heardLevel(const std::vector<float>& samples) {
    double energy = 0;
    const int frames = SAMPLE_RATE * 3 / 10;
    for (int frame = 0; frame < frames; ++frame) energy += samples[frame] * samples[frame];
    return std::sqrt(energy / frames);
}

void matchKeepsTheEqGentle() {
    Tone harsh{};
    for (int band = 0; band < LeadAmp::TONE_BANDS; ++band) harsh[band] = band % 2 ? 12.0f : -12.0f;
    requireGentleTone(match(1.0f, harsh, 8).tone);
    requireGentleTone(match(1.0f, {}, 8, 0.0f, 0.0f, 0.3).tone);
}

void matchDoesNotBoostLowNotes() {
    Tone bassy{};
    for (int band = 0; band <= 7; ++band) bassy[band] = 12.0f;
    for (int band = 9; band <= 14; ++band) bassy[band] = -12.0f;
    const auto matched = match(1.0f, bassy, 8);
    REQUIRE(matched.result == YARG_AUDIO_OK);
    requireGentleTone(matched.tone);
    for (double hz : {82.4, 110.0, 146.8}) {
        const auto low = pluck(hz, SAMPLE_RATE * 2 / 5);
        const double fitted = heardLevel(monoRender(low, matched.drive, matched.tone)) * matched.levelGain;
        const double plain = heardLevel(monoRender(low, 1.0f, {}));
        REQUIRE(20 * std::log10(fitted / plain) < 3.5);
    }
}

void matchIgnoresALimiter() {
    for (float drive : {0.5f, 3.0f}) {
        const auto matched = match(drive, {}, 8, 0.0f, 0.0f, 0.0, {}, 0, SAMPLE_RATE * 2 / 5, true);
        REQUIRE(matched.result == YARG_AUDIO_OK);
        REQUIRE(std::abs(std::log2(matched.drive / drive)) < 0.5);
    }
}

void matchFindsDriveThroughEachCabinet() {
    const std::vector<std::vector<float>> cabinets{resonantCabinet(500), resonantCabinet(2500)};
    for (int stemCabinet = 0; stemCabinet < 2; ++stemCabinet) {
        const auto matched = match(3.0f, {}, 8, 0.0f, 0.0f, 0.0, cabinets, stemCabinet);
        REQUIRE(matched.result == YARG_AUDIO_OK);
        REQUIRE(matched.cabinet == static_cast<std::uint32_t>(stemCabinet));
        REQUIRE(std::abs(std::log2(matched.drive / 3.0f)) < 0.5);
    }
}

void matchFallsBackWhenTheFadeMakesNoSense() {
    std::vector<float> stem, di;
    std::vector<std::uint32_t> frames;
    for (int note = 0; note < 8; ++note) {
        const auto played = pluck(196 * std::pow(2.0, note / 4.0), SAMPLE_RATE * 2 / 5);
        auto heard = monoRender(played, 1.0f, {});
        std::reverse(heard.begin(), heard.end());
        di.insert(di.end(), played.begin(), played.end());
        stem.insert(stem.end(), heard.begin(), heard.end());
        frames.push_back(static_cast<std::uint32_t>(played.size()));
    }
    auto fitted = LeadAmp::defaultTone();
    REQUIRE(yarg::audio::leadAmpMatch(SAMPLE_RATE, nullptr, 0, stem.data(), di.data(), frames.data(), frames.size(),
        &fitted) == YARG_AUDIO_OK);
    REQUIRE(fitted.drive == 1.0f);
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
    toneBandBoostsItsFrequency();
    invalidToneIsRejected();
    matchKeepsTheDefaultTone();
    matchFindsDriveAndTone();
    matchNeedsEnoughNotes();
    matchIgnoresLowBleed();
    wahSweepsDownAfterThePick();
    wahRestartsOnTheNextPick();
    matchFindsWah();
    toneSwitchesCabinet();
    echoFollowsTheTone();
    matchPicksTheCabinet();
    matchWorksOnShortNotes();
    matchKeepsTheEqGentle();
    matchDoesNotBoostLowNotes();
    matchIgnoresALimiter();
    matchFindsDriveThroughEachCabinet();
    matchFallsBackWhenTheFadeMakesNoSense();
}
