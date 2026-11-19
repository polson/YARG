#pragma once

#include "yarg_audio.h"

#include <cstddef>
#include <cstdint>

namespace yarg::audio {

int soloSpaceMatch(float sampleRate, const float* region, std::uint64_t frames,
    const double* pickSeconds, std::size_t pickCount, yarg_solo_space* space) noexcept;

}
