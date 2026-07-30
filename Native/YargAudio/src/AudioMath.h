#pragma once

#include <cstddef>

namespace yarg::audio {

inline void mixAdd(float* output, const float* input, std::size_t samples) noexcept {
    for (std::size_t i = 0; i < samples; ++i) output[i] += input[i];
}

inline void applyGain(float* samples, std::size_t count, float gain) noexcept {
    if (gain == 1.0f) return;
    for (std::size_t i = 0; i < count; ++i) samples[i] *= gain;
}

} // namespace yarg::audio
