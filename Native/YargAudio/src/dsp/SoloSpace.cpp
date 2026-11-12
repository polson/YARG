#include "dsp/SoloSpace.h"

#include "dsp/FreeverbDsp.h"
#include "dsp/LeadAmpDsp.h"

#include <algorithm>
#include <cmath>
#include <new>
#include <vector>

namespace {

constexpr double FRAME_SECONDS = 0.01;
constexpr double MIN_GAP_SECONDS = 0.5;
constexpr double MAX_GAP_SECONDS = 1.5;
constexpr std::size_t MIN_GAPS = 3;
constexpr double RELEASE_SEARCH_SECONDS = 0.1;
constexpr int RELEASE_FRAMES = 3;
constexpr double MIN_RELEASE_DROP_DB = 6.0;
constexpr int DROP_FRAMES = 5;
constexpr int TAIL_START_FRAMES = 3;
constexpr int TAIL_FRAMES = 40;
constexpr double TAIL_FALL_DB = 15.0;
constexpr double MAX_TAIL_SLOPE_DB = -10.0;
constexpr double DRY_DROP_DB = 45.0;
constexpr int SUSTAIN_FRAMES = 12;
constexpr double MAX_SUSTAIN_FADE_DB = -60.0;
constexpr double DRY_SLOPE_DB = -300.0;
constexpr double ECHO_SEARCH_SECONDS = 0.12;
constexpr int RISE_FRAMES = 2;
constexpr double MAX_RISE_DB = 12.0;
constexpr double MIN_ECHO_RISE_DB = 0.5;
constexpr double ECHO_FLOOR_DB = 40.0;
constexpr double ECHO_PROMINENCE = 4.0;
constexpr int ECHO_NEIGHBOUR_FRAMES = 2;
constexpr int ECHO_LOCAL_FRAMES = 20;
constexpr float REVERB_DAMP = 0.5f;
constexpr float REVERB_WIDTH = 1.0f;
constexpr float MAX_REVERB_WET = 1.5f;
constexpr float FIT_START_WET = 0.5f;
constexpr int FIT_STEPS = 12;
constexpr double CALIBRATION_NOTE_SECONDS = 0.4;
constexpr double CALIBRATION_RELEASE_SECONDS = 0.015;
constexpr double CALIBRATION_SECONDS = 1.2;
constexpr double CALIBRATION_LOW_PASS_HZ = 3000.0;
constexpr double CALIBRATION_LEVEL = 0.1;
constexpr double PI = 3.14159265358979323846;
constexpr double QUIET_RMS = 1e-6;

struct Release {
    int frame = -1;
    double drop = 0.0;
    double slope = 0.0;
    double sustain = 0.0;
};

double median(std::vector<double> values) {
    const auto middle = values.begin() + static_cast<std::ptrdiff_t>(values.size() / 2);
    std::nth_element(values.begin(), middle, values.end());
    return *middle;
}

std::vector<double> envelope(const float* samples, std::size_t frames, std::size_t frameLength) {
    std::vector<double> decibels(frames / frameLength);
    for (std::size_t frame = 0; frame < decibels.size(); ++frame) {
        double energy = 0.0;
        for (std::size_t sample = frame * frameLength; sample < (frame + 1) * frameLength; ++sample) {
            energy += static_cast<double>(samples[sample]) * samples[sample];
        }
        decibels[frame] = 20.0 * std::log10(std::max(std::sqrt(energy / frameLength), QUIET_RMS));
    }
    return decibels;
}

bool measureRelease(const std::vector<double>& decibels, int searchStart, Release& release) {
    const int last = static_cast<int>(decibels.size()) - RELEASE_FRAMES - DROP_FRAMES;
    int best = -1;
    double bestDrop = 0.0;
    for (int frame = std::max(searchStart, RELEASE_FRAMES); frame < last; ++frame) {
        const double drop = decibels[frame - 1] - decibels[frame + RELEASE_FRAMES - 1];
        if (drop > bestDrop) {
            bestDrop = drop;
            best = frame;
        }
    }
    if (best < 0 || bestDrop < MIN_RELEASE_DROP_DB) return false;
    release.frame = best;

    double before = 0.0;
    for (int frame = best - RELEASE_FRAMES; frame < best; ++frame) {
        before += decibels[frame];
    }
    before /= RELEASE_FRAMES;
    release.drop = before - decibels[best + DROP_FRAMES];
    const int sustainStart = std::max(0, best - RELEASE_FRAMES - SUSTAIN_FRAMES);
    const int sustainEnd = best - RELEASE_FRAMES;
    release.sustain = sustainEnd > sustainStart
        ? (decibels[sustainEnd] - decibels[sustainStart]) / ((sustainEnd - sustainStart) * FRAME_SECONDS)
        : 0.0;

    const int start = best + TAIL_START_FRAMES;
    const double floor = before - DRY_DROP_DB;
    if (decibels[start] <= floor) {
        release.slope = DRY_SLOPE_DB;
        return true;
    }
    int end = start;
    while (end + 1 < static_cast<int>(decibels.size()) && end - start < TAIL_FRAMES
        && decibels[end] > decibels[start] - TAIL_FALL_DB && decibels[end] > floor) {
        ++end;
    }
    if (end == start) return false;
    release.slope = (decibels[end] - decibels[start]) / ((end - start) * FRAME_SECONDS);
    return release.slope < MAX_TAIL_SLOPE_DB;
}

class Calibration {
public:
    Calibration(float sampleRate, double sustainDbPerSecond) : sampleRate_(sampleRate) {
        const double fade = std::clamp(sustainDbPerSecond, MAX_SUSTAIN_FADE_DB, 0.0);
        const auto frames = static_cast<std::size_t>(CALIBRATION_SECONDS * sampleRate);
        const auto held = static_cast<std::size_t>(CALIBRATION_NOTE_SECONDS * sampleRate);
        const auto mute = static_cast<std::size_t>(CALIBRATION_RELEASE_SECONDS * sampleRate);
        const double smoothing = 1.0 - std::exp(-2.0 * PI * CALIBRATION_LOW_PASS_HZ / sampleRate);
        dry_.assign(frames * 2, 0.0f);
        std::uint32_t noise = 0x9E3779B9u;
        double low = 0.0;
        for (std::size_t frame = 0; frame < held + mute && frame < frames; ++frame) {
            noise ^= noise << 13;
            noise ^= noise >> 17;
            noise ^= noise << 5;
            low += (static_cast<double>(noise) / 2147483648.0 - 1.0 - low) * smoothing;
            const double level = std::pow(10.0, fade * frame / sampleRate / 20.0)
                * (frame < held ? 1.0 : 1.0 - static_cast<double>(frame - held) / mute);
            dry_[frame * 2] = dry_[frame * 2 + 1] = static_cast<float>(CALIBRATION_LEVEL * level * low / 0.3);
        }
    }

    bool measure(float wet, float room, Release& release) const {
        auto samples = dry_;
        if (!yarg::audio::freeverbRender(static_cast<std::uint32_t>(sampleRate_), 2, 1.0f, wet, room, REVERB_DAMP,
                REVERB_WIDTH, samples.data(), samples.size() / 2)) {
            return false;
        }
        std::vector<float> mono(samples.size() / 2);
        for (std::size_t frame = 0; frame < mono.size(); ++frame) {
            mono[frame] = 0.5f * (samples[frame * 2] + samples[frame * 2 + 1]);
        }
        const auto frameLength = static_cast<std::size_t>(FRAME_SECONDS * sampleRate_);
        const int searchStart = static_cast<int>(RELEASE_SEARCH_SECONDS / FRAME_SECONDS);
        return measureRelease(envelope(mono.data(), mono.size(), frameLength), searchStart, release);
    }

private:
    float sampleRate_;
    std::vector<float> dry_;
};

template <typename Measure>
float bisect(float low, float high, double target, bool rising, Measure measure) {
    for (int step = 0; step < FIT_STEPS; ++step) {
        const float middle = 0.5f * (low + high);
        double value = 0.0;
        measure(middle, value);
        if ((value < target) == rising) {
            low = middle;
        } else {
            high = middle;
        }
    }
    return 0.5f * (low + high);
}

void fitReverb(float sampleRate, double slope, double drop, double sustain, yarg_solo_space& space) {
    const Calibration calibration(sampleRate, sustain);
    float wet = FIT_START_WET;
    float room = 0.0f;
    const auto fitRoom = [&] {
        return bisect(0.0f, 1.0f, slope, true, [&](float candidate, double& value) {
            Release release;
            value = calibration.measure(wet, candidate, release) ? release.slope : 0.0;
        });
    };
    room = fitRoom();
    wet = bisect(0.0f, MAX_REVERB_WET, drop, false, [&](float candidate, double& value) {
        Release release;
        value = calibration.measure(candidate, room, release) ? release.drop : 0.0;
    });
    room = fitRoom();
    space.reverb_wet = wet;
    space.reverb_room = room;
}

}

namespace yarg::audio {

int soloSpaceMatch(float sampleRate, const float* region, std::uint64_t frames,
    const double* pickSeconds, std::size_t pickCount, yarg_solo_space* space) noexcept {
    if (!space || space->size < sizeof(yarg_solo_space) || !region || !pickSeconds
        || !std::isfinite(sampleRate) || sampleRate <= 0.0f) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }

    try {
        const auto frameLength = static_cast<std::size_t>(FRAME_SECONDS * sampleRate);
        const double regionSeconds = static_cast<double>(frames) / sampleRate;
        const int minLag = static_cast<int>(std::ceil(ECHO_SEARCH_SECONDS / FRAME_SECONDS));
        const int maxLag = static_cast<int>(LeadAmp::MAX_ECHO_SECONDS / FRAME_SECONDS);
        std::vector<double> riseSums(maxLag + 1, 0.0);
        std::vector<std::size_t> riseCounts(maxLag + 1, 0);
        std::vector<std::vector<double>> gapLevels;
        std::vector<double> attacks, slopes, drops, sustains;
        for (std::size_t pick = 0; pick < pickCount; ++pick) {
            const double start = pickSeconds[pick];
            const double next = pick + 1 < pickCount ? pickSeconds[pick + 1] : regionSeconds;
            const double gap = std::min(next, regionSeconds) - start;
            if (start < 0.0 || gap < MIN_GAP_SECONDS) continue;

            const auto first = static_cast<std::size_t>(start * sampleRate);
            const auto length = static_cast<std::size_t>(std::min(gap, MAX_GAP_SECONDS) * sampleRate);
            auto decibels = envelope(region + first, length, frameLength);
            const int count = static_cast<int>(decibels.size());
            double attack = decibels[0];
            for (int frame = 1; frame < std::min(count, RELEASE_FRAMES); ++frame) {
                attack = std::max(attack, decibels[frame]);
            }
            for (int lag = minLag; lag <= std::min(maxLag, count - 1); ++lag) {
                if (decibels[lag] < attack - ECHO_FLOOR_DB) continue;
                riseSums[lag] += std::clamp(decibels[lag] - decibels[lag - RISE_FRAMES], 0.0, MAX_RISE_DB);
                ++riseCounts[lag];
            }

            Release release;
            if (measureRelease(decibels, static_cast<int>(RELEASE_SEARCH_SECONDS / FRAME_SECONDS), release)) {
                slopes.push_back(release.slope);
                drops.push_back(release.drop);
                sustains.push_back(release.sustain);
            }
            attacks.push_back(attack);
            gapLevels.push_back(std::move(decibels));
        }
        if (gapLevels.size() < MIN_GAPS) return YARG_AUDIO_ERROR_INVALID_STATE;

        int echoLag = -1;
        double echoScore = 0.0;
        for (int lag = minLag; lag <= maxLag; ++lag) {
            if (riseCounts[lag] < MIN_GAPS) continue;
            const double rise = riseSums[lag] / riseCounts[lag];
            if (rise < MIN_ECHO_RISE_DB) continue;
            double spread = 0.0;
            int spreadCount = 0;
            for (int other = std::max(minLag, lag - ECHO_LOCAL_FRAMES);
                 other <= std::min(maxLag, lag + ECHO_LOCAL_FRAMES); ++other) {
                if (riseCounts[other] < MIN_GAPS || std::abs(other - lag) <= ECHO_NEIGHBOUR_FRAMES) continue;
                const double nearby = riseSums[other] / riseCounts[other];
                spread += nearby * nearby;
                ++spreadCount;
            }
            spread = spreadCount > 0 ? std::sqrt(spread / spreadCount) : 0.0;
            const double score = spread > 0.0 ? rise / spread : HUGE_VAL;
            if (score > echoScore) {
                echoLag = lag;
                echoScore = score;
            }
        }

        if (echoLag >= 0 && echoScore >= ECHO_PROMINENCE) {
            double added = 0.0;
            int heardGaps = 0;
            for (std::size_t gap = 0; gap < gapLevels.size(); ++gap) {
                const auto& decibels = gapLevels[gap];
                if (echoLag >= static_cast<int>(decibels.size())) continue;
                added += (std::pow(10.0, decibels[echoLag] / 10.0) - std::pow(10.0, decibels[echoLag - RISE_FRAMES] / 10.0))
                    / std::pow(10.0, attacks[gap] / 10.0);
                ++heardGaps;
            }
            space->echo_seconds = static_cast<float>(echoLag * FRAME_SECONDS);
            space->echo_level = heardGaps > 0
                ? std::clamp(static_cast<float>(std::sqrt(std::max(0.0, added / heardGaps))), 0.0f, 1.0f)
                : 0.0f;
        } else {
            space->echo_level = 0.0f;
        }

        if (slopes.size() >= MIN_GAPS) {
            fitReverb(sampleRate, median(slopes), median(drops), median(sustains), *space);
        }
        return YARG_AUDIO_OK;
    } catch (const std::bad_alloc&) {
        return YARG_AUDIO_ERROR_INTERNAL;
    }
}

}
