#include "Test.h"

#include <iostream>

int main() {
    runAudioRingBufferTests();
    runBassBindingTests();
    runGainDspTests();
    runFreeverbDspTests();
    runDattorroReverbDspTests();
    runNoiseGateDspTests();
    runOverdriveDspTests();
    runNoiseMorphTests();
    runYargStretchTests();
    runSineSynthDspTests();
    runScheduledSampleSourceTests();
    runNativeOneShotStreamTests();
    runStretchTempoStreamTests();
    runStretchRenderTests();
    runRenderAheadMixerTests();
    runReadAheadStreamTests();
    std::cout << "YargAudio native tests passed\n";
    return 0;
}
