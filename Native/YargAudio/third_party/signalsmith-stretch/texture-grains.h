#ifndef YARG_TEXTURE_GRAINS_H
#define YARG_TEXTURE_GRAINS_H

#include <cstring>
#include "signalsmith-linear/stft.h"
#include <algorithm>
#include <cmath>
#include <complex>
#include <vector>

namespace signalsmith { namespace stretch {

template<typename Sample>
class TextureGrains {
    using Complex = std::complex<Sample>;
    using STFT = signalsmith::linear::DynamicSTFT<Sample, false, true>;
    signalsmith::linear::RealFFT<Sample, false, true> fft;
    std::vector<Complex> spectrum;
    std::vector<Sample> waveform, previous, fade, inverseWindow, output;
    int channels = 0;
    int hop = 0;
    int outputPosition = 0;
    bool fullSearch = false;

public:
    void configure(const STFT &analysis, int count, bool fullSearchGrains = false) {
        channels = count;
        fullSearch = fullSearchGrains;
        hop = int(analysis.defaultInterval());
        fft.resize(analysis.fftSamples());
        spectrum.resize(analysis.bands());
        waveform.resize(channels * analysis.fftSamples());
        previous.assign(channels * hop, Sample(0));
        output.assign(channels * analysis.blockSamples(), Sample(0));
        outputPosition = 0;
        fade.resize(hop);
        inverseWindow.resize(analysis.blockSamples());
        for (int i = 0; i < hop; ++i) {
            fade[i] = std::sin((i + Sample(0.5)) * Sample(1.5707963267948966) / hop);
        }
        for (int i = 0; i < int(analysis.blockSamples()); ++i) {
            inverseWindow[i] = Sample(1) / (analysis.fftSamples() * analysis.analysisWindow()[i]);
        }
    }

    void reset() {
        std::fill(previous.begin(), previous.end(), Sample(0));
        std::fill(output.begin(), output.end(), Sample(0));
        outputPosition = 0;
    }

    template<class Input>
    void add(const STFT &analysis, Input input, const std::vector<Sample> &mask) {
        const int size = int(analysis.fftSamples());
        const int center = int(analysis.analysisOffset());
        const int block = int(analysis.blockSamples());
        for (int c = 0; c < channels; ++c) {
            for (int b = 0; b < int(spectrum.size()); ++b) {
                spectrum[b] = input(c, b) * std::sqrt(mask[b]);
            }
            auto *wave = waveform.data() + c * size;
            fft.ifft(spectrum.data(), wave);
            for (int i = -hop - hop / 4; i < hop + hop / 4; ++i) {
                const int index = i < 0 ? size + i : i;
                wave[index] *= (i < 0 ? -1 : 1) * inverseWindow[center + i];
            }
        }
        int bestOffset = 0;
        double bestScore = 0;
        const int searchStep = fullSearch ? 1 : std::max(1, hop / 16);
        for (int offset = -hop / 4; offset <= hop / 4; offset += searchStep) {
            double cross = 0;
            double oldPower = 0;
            double newPower = 0;
            for (int c = 0; c < channels; ++c) {
                const auto *wave = waveform.data() + c * size;
                const auto *tail = previous.data() + c * hop;
                for (int i = 0; i < hop; ++i) {
                    const int index = (size - hop + offset + i) % size;
                    const double next = wave[index];
                    cross += tail[i] * next;
                    oldPower += double(tail[i]) * tail[i];
                    newPower += next * next;
                }
            }
            const double score = cross / std::sqrt(oldPower * newPower + 1e-60);
            if (score > bestScore) {
                bestScore = score;
                bestOffset = offset;
            }
        }
        const Sample correlation = Sample(std::clamp(bestScore, 0.0, 1.0));
        const int start = outputPosition + int(analysis.synthesisOffset()) - hop;
        for (int c = 0; c < channels; ++c) {
            const auto *wave = waveform.data() + c * size;
            auto *tail = previous.data() + c * hop;
            auto *buffer = output.data() + c * block;
            for (int i = 0; i < hop; ++i) {
                const Sample rise = fade[i];
                const Sample fall = fade[hop - 1 - i];
                const Sample next = wave[(size - hop + bestOffset + i) % size];
                buffer[(start + i) % block] += (tail[i] * fall + next * rise) /
                    std::sqrt(Sample(1) + 2 * correlation * rise * fall);
                tail[i] = wave[(size + bestOffset + i) % size];
            }
        }
    }

    Sample read(int channel) const {
        return output[channel * inverseWindow.size() + outputPosition];
    }

    void advance() {
        for (int c = 0; c < channels; ++c) {
            output[c * inverseWindow.size() + outputPosition] = 0;
        }
        outputPosition = (outputPosition + 1) % inverseWindow.size();
    }
};

}}
#endif
