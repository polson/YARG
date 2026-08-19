#include "Test.h"

#include <iostream>

int main() {
    runAudioRingBufferTests();
    runBassBindingTests();
    runGainDspTests();
    runFreeverbDspTests();
    runNoiseGateDspTests();
    runScheduledSampleSourceTests();
    runNativeOneShotStreamTests();
    runRenderAheadMixerTests();
    runReadAheadStreamTests();
    std::cout << "YargAudio native tests passed\n";
    return 0;
}
