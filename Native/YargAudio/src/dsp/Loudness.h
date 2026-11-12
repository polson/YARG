#pragma once

#include <cmath>
#include <cstddef>

namespace yarg::audio {

inline double kWeightedLevel(const float* samples, std::size_t frames, float sampleRate) noexcept {
    constexpr double PI = 3.14159265358979323846;
    constexpr double SHELF_HZ = 1681.974450955533;
    constexpr double SHELF_DB = 3.999843853973347;
    constexpr double SHELF_Q = 0.7071752369554196;
    constexpr double HIGH_PASS_HZ = 38.13547087602444;
    constexpr double HIGH_PASS_Q = 0.5003270373238773;

    if (!samples || frames == 0 || !(sampleRate > 0.0f)) return 0.0;

    const double shelfW0 = 2.0 * PI * SHELF_HZ / sampleRate;
    const double shelfAlpha = std::sin(shelfW0) / (2.0 * SHELF_Q);
    const double amplitude = std::pow(10.0, SHELF_DB / 40.0);
    const double shelfCos = std::cos(shelfW0);
    const double root = 2.0 * std::sqrt(amplitude) * shelfAlpha;
    const double shelfA0 = (amplitude + 1.0) - (amplitude - 1.0) * shelfCos + root;
    const double s0 = amplitude * ((amplitude + 1.0) + (amplitude - 1.0) * shelfCos + root) / shelfA0;
    const double s1 = -2.0 * amplitude * ((amplitude - 1.0) + (amplitude + 1.0) * shelfCos) / shelfA0;
    const double s2 = amplitude * ((amplitude + 1.0) + (amplitude - 1.0) * shelfCos - root) / shelfA0;
    const double sa1 = 2.0 * ((amplitude - 1.0) - (amplitude + 1.0) * shelfCos) / shelfA0;
    const double sa2 = ((amplitude + 1.0) - (amplitude - 1.0) * shelfCos - root) / shelfA0;

    const double passW0 = 2.0 * PI * HIGH_PASS_HZ / sampleRate;
    const double passAlpha = std::sin(passW0) / (2.0 * HIGH_PASS_Q);
    const double passCos = std::cos(passW0);
    const double passA0 = 1.0 + passAlpha;
    const double p0 = (1.0 + passCos) / 2.0 / passA0;
    const double p1 = -(1.0 + passCos) / passA0;
    const double pa1 = -2.0 * passCos / passA0;
    const double pa2 = (1.0 - passAlpha) / passA0;

    double shelf1 = 0.0, shelf2 = 0.0, pass1 = 0.0, pass2 = 0.0, energy = 0.0;
    for (std::size_t frame = 0; frame < frames; ++frame) {
        const double input = samples[frame];
        const double shelved = s0 * input + shelf1;
        shelf1 = s1 * input - sa1 * shelved + shelf2;
        shelf2 = s2 * input - sa2 * shelved;
        const double passed = p0 * shelved + pass1;
        pass1 = p1 * shelved - pa1 * passed + pass2;
        pass2 = p0 * shelved - pa2 * passed;
        energy += passed * passed;
    }
    return std::sqrt(energy / static_cast<double>(frames));
}

}
