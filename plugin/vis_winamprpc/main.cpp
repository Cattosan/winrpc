#include <windows.h>
#include "vis.h"

HANDLE hPipe = INVALID_HANDLE_VALUE;

void Config(struct winampVisModule *this_mod) {
    MessageBox(this_mod->hwndParent, "WinampRPC Visualizer Bridge\nSends real-time 24-band FFT data over Named Pipe to WinampRPC UI.", "Configuration", MB_OK);
}

int Init(struct winampVisModule *this_mod) {
    hPipe = CreateNamedPipeA(
        "\\\\.\\pipe\\WinampRPC_Vis",
        PIPE_ACCESS_OUTBOUND,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT,
        1,
        1024,
        1024,
        0,
        NULL
    );
    return 0; // success
}

int Render(struct winampVisModule *this_mod) {
    if (hPipe == INVALID_HANDLE_VALUE) return 0;
    
    unsigned char output[24];
    
    // Logarithmic mapping: lower rods take fewer bands (bass), higher rods take more (treble)
    // Total sum = 576
    int bandMappings[24] = {
        1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 
        3, 4, 5, 7, 9, 12, 16, 21, 28, 38,
        50, 68, 92, 207
    };
    
    int currentBand = 0;
    for (int i = 0; i < 24; i++) {
        int maxVal = 0;
        int bandsForThisRod = bandMappings[i];
        
        for (int j = 0; j < bandsForThisRod; j++) {
            if (currentBand >= 576) break;
            int avg = (this_mod->spectrumData[0][currentBand] + this_mod->spectrumData[1][currentBand]) / 2;
            if (avg > maxVal) maxVal = avg;
            currentBand++;
        }
        
        // Apply a 1.5x punch multiplier
        float scaledVal = (float)maxVal * 1.5f;
        if (scaledVal > 255.0f) scaledVal = 255.0f;
        
        output[i] = (unsigned char)scaledVal;
    }
    
    // C++ is Server, C# is Client.
    // Ensure we are connected
    ConnectNamedPipe(hPipe, NULL); 
    // Ignore error, if client connects it succeeds, if already connected it fails with ERROR_PIPE_CONNECTED
    
    DWORD bytesWritten = 0;
    WriteFile(hPipe, output, 24, &bytesWritten, NULL);
    
    return 0; // return 0 to keep running
}

void Quit(struct winampVisModule *this_mod) {
    if (hPipe != INVALID_HANDLE_VALUE) {
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
        hPipe = INVALID_HANDLE_VALUE;
    }
}

winampVisModule mod = {
    "WinampRPC Visualizer Bridge",
    NULL, // hwndParent
    NULL, // hDllInstance
    0,    // sRate
    0,    // nCh
    15,   // latencyMs
    15,   // delayMs
    2,    // spectrumNch (Request Left and Right channels)
    0,    // waveformNch (Don't need waveform)
    {0},  // spectrumData
    {0},  // waveformData
    Config,
    Init,
    Render,
    Quit,
    NULL  // userData
};

winampVisModule* getModule(int which) {
    if (which == 0) return &mod;
    return NULL;
}

winampVisHeader hdr = {
    VIS_HDRVER,
    "WinampRPC Vis Plugin",
    getModule
};

#ifdef __cplusplus
extern "C" {
#endif
__declspec(dllexport) winampVisHeader* winampVisGetHeader(HWND hwndParent) {
    return &hdr;
}
#ifdef __cplusplus
}
#endif
