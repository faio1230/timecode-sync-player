#define NOMINMAX
#include "SpoutDX.h"
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;

namespace {

std::atomic<bool> stopRequested{false};

BOOL WINAPI HandleConsoleSignal(DWORD signal)
{
    if (signal == CTRL_C_EVENT || signal == CTRL_BREAK_EVENT) {
        stopRequested = true;
        return TRUE;
    }
    return FALSE;
}

long long Qpc()
{
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}

std::string JsonString(const std::string& value)
{
    std::ostringstream output;
    output << '"';
    for (unsigned char character : value) {
        if (character == '"' || character == '\\') output << '\\' << character;
        else if (character < 0x20) {
            output << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                   << static_cast<int>(character) << std::dec;
        }
        else output << character;
    }
    output << '"';
    return output.str();
}

unsigned long long FileTimeValue(FILETIME value)
{
    return (static_cast<unsigned long long>(value.dwHighDateTime) << 32) |
           value.dwLowDateTime;
}

double CpuSeconds()
{
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) return 0.0;
    return (FileTimeValue(kernel) + FileTimeValue(user)) / 10'000'000.0;
}

bool IsSupported32BitFormat(DXGI_FORMAT format)
{
    return format == DXGI_FORMAT_B8G8R8A8_UNORM ||
           format == DXGI_FORMAT_B8G8R8X8_UNORM ||
           format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
           format == DXGI_FORMAT_R8G8B8A8_UNORM ||
           format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
}

struct PixelSample {
    bool available = false;
    std::uint64_t hash = 0;
    int blackPixels = 0;
    int sampledPixels = 0;
    double gpuReadbackMs = 0.0;
};

class TinyTextureSampler {
public:
    PixelSample Capture(ID3D11Device* device, ID3D11DeviceContext* context,
                        ID3D11Texture2D* source, long long frequency)
    {
        if (!device || !context || !source) return {};
        D3D11_TEXTURE2D_DESC sourceDescription{};
        source->GetDesc(&sourceDescription);
        if (!sourceDescription.Width || !sourceDescription.Height ||
            sourceDescription.SampleDesc.Count != 1 ||
            !IsSupported32BitFormat(sourceDescription.Format)) return {};

        if (!_staging || sourceDescription.Format != _format || sourceDescription.Width != _width) {
            _staging.Reset();
            D3D11_TEXTURE2D_DESC stagingDescription{};
            stagingDescription.Width = sourceDescription.Width;
            stagingDescription.Height = SampleRows;
            stagingDescription.MipLevels = 1;
            stagingDescription.ArraySize = 1;
            stagingDescription.Format = sourceDescription.Format;
            stagingDescription.SampleDesc.Count = 1;
            stagingDescription.Usage = D3D11_USAGE_STAGING;
            stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            HRESULT created = device->CreateTexture2D(&stagingDescription, nullptr, &_staging);
            if (FAILED(created)) return {};
            _format = sourceDescription.Format;
            _width = sourceDescription.Width;
        }

        long long started = Qpc();
        for (UINT row = 0; row < SampleRows; ++row) {
            UINT sourceY = sourceDescription.Height == 1
                ? 0 : (sourceDescription.Height - 1) * row / (SampleRows - 1);
            D3D11_BOX box{ 0, sourceY, 0, sourceDescription.Width, sourceY + 1, 1 };
            context->CopySubresourceRegion(_staging.Get(), 0, 0, row, 0, source, 0, &box);
        }

        D3D11_MAPPED_SUBRESOURCE mapped{};
        HRESULT mappedResult = context->Map(_staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        long long mappedAt = Qpc();
        if (FAILED(mappedResult)) return {};

        std::uint64_t hash = 14695981039346656037ull;
        int blackPixels = 0;
        for (UINT y = 0; y < SampleRows; ++y) {
            const auto* row = static_cast<const unsigned char*>(mapped.pData) + y * mapped.RowPitch;
            for (UINT x = 0; x < sourceDescription.Width; ++x) {
                const unsigned char* pixel = row + x * 4;
                for (int channel = 0; channel < 4; ++channel) {
                    hash ^= pixel[channel];
                    hash *= 1099511628211ull;
                }
                if (pixel[0] <= 16 && pixel[1] <= 16 && pixel[2] <= 16) ++blackPixels;
            }
        }
        context->Unmap(_staging.Get(), 0);
        return { true, hash, blackPixels,
                 static_cast<int>(sourceDescription.Width * SampleRows),
                 (mappedAt - started) * 1000.0 / frequency };
    }

private:
    static constexpr UINT SampleRows = 9;
    ComPtr<ID3D11Texture2D> _staging;
    DXGI_FORMAT _format = DXGI_FORMAT_UNKNOWN;
    UINT _width = 0;
};

struct Options {
    std::string sender;
    std::string output;
    std::string stopFile;
    double durationSeconds = 120.0;
    int pollMilliseconds = 1;
    int sampleMilliseconds = 1000;
};

Options ParseOptions(int argc, char** argv)
{
    Options options;
    for (int index = 1; index < argc; ++index) {
        std::string option = argv[index];
        if (index + 1 >= argc) throw std::runtime_error("Missing option value: " + option);
        std::string value = argv[++index];
        if (option == "--sender") options.sender = value;
        else if (option == "--output") options.output = value;
        else if (option == "--stop-file") options.stopFile = value;
        else if (option == "--duration") options.durationSeconds = std::stod(value);
        else if (option == "--poll-ms") options.pollMilliseconds = std::stoi(value);
        else if (option == "--sample-ms") options.sampleMilliseconds = std::stoi(value);
        else throw std::runtime_error("Unknown option: " + option);
    }

    const std::string safeSenderCharacters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-. ";
    if (options.sender.empty() || options.sender.size() > 220 ||
        options.sender.find_first_not_of(safeSenderCharacters) != std::string::npos ||
        options.output.empty() || options.stopFile.empty() ||
        !std::isfinite(options.durationSeconds) || options.durationSeconds < 1.0 ||
        options.durationSeconds > 46'800.0 || options.pollMilliseconds < 1 ||
        options.pollMilliseconds > 100 || options.sampleMilliseconds < 16 ||
        options.sampleMilliseconds > 60'000) {
        throw std::runtime_error(
            "Usage: SpoutContinuityProbe --sender NAME --output NEW.jsonl "
            "--stop-file NEW.stop [--duration 120] [--poll-ms 1] [--sample-ms 1000]");
    }
    return options;
}

void ReserveOutput(const std::string& output)
{
    HANDLE file = CreateFileA(output.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                              CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        throw std::runtime_error("Cannot reserve new output file");
    CloseHandle(file);
}

} // namespace

int main(int argc, char** argv)
{
    LARGE_INTEGER frequencyValue{};
    QueryPerformanceFrequency(&frequencyValue);
    const long long frequency = frequencyValue.QuadPart;
    const long long processStarted = Qpc();
    std::ofstream log;
    std::string stage = "arguments";
    std::unique_ptr<spoutDX> receiverOwner;

    try {
        Options options = ParseOptions(argc, argv);
        stage = "reserve-output";
        ReserveOutput(options.output);
        log.open(options.output, std::ios::out | std::ios::trunc);
        if (!log) throw std::runtime_error("Cannot open reserved output file");
        log << std::boolalpha << std::setprecision(16);
        log << "{\"event\":\"start\",\"qpc\":" << processStarted
            << ",\"qpcFrequency\":" << frequency
            << ",\"pid\":" << GetCurrentProcessId()
            << ",\"sender\":" << JsonString(options.sender)
            << ",\"pollMs\":" << options.pollMilliseconds
            << ",\"sampleMs\":" << options.sampleMilliseconds
            << ",\"durationSeconds\":" << options.durationSeconds
            << ",\"transport\":\"independent Spout ReceiveTexture; sender frame counter at every poll; nine full-width GPU readback rows per sample interval\"}\n";
        log.flush();

        SetConsoleCtrlHandler(HandleConsoleSignal, TRUE);
        timeBeginPeriod(1);
        struct TimerResolutionGuard {
            ~TimerResolutionGuard() { timeEndPeriod(1); }
        } timerResolutionGuard;

        stage = "open-directx";
        receiverOwner = std::make_unique<spoutDX>();
        spoutDX& receiver = *receiverOwner;
        receiver.SetReceiverName(options.sender.c_str());
        if (!receiver.OpenDirectX11()) throw std::runtime_error("OpenDirectX11 failed");
        receiver.SetAdapterAuto(false);
        ID3D11Device* device = receiver.GetDX11Device();
        ID3D11DeviceContext* context = receiver.GetDX11Context();
        if (!device || !context) throw std::runtime_error("Receiver device/context unavailable");

        TinyTextureSampler sampler;
        bool connectedEver = false;
        bool wasConnected = false;
        bool frameCounterAvailable = false;
        long lastCounter = 0;
        long long lastFrameQpc = 0;
        long long lastFlushQpc = processStarted;
        long long lastHeartbeatQpc = processStarted;
        long long lastSampleQpc = 0;
        unsigned int width = 0, height = 0;
        DWORD format = 0;
        HANDLE shareHandle = nullptr;
        unsigned long long polls = 0;
        unsigned long long receiveFailures = 0;
        unsigned long long uniqueFrames = 0;
        unsigned long long observedIntervals = 0;
        unsigned long long counterJumps = 0;
        unsigned long long missedSenderFrames = 0;
        unsigned long long counterResets = 0;
        unsigned long long disconnects = 0;
        unsigned long long metadataChanges = 0;
        unsigned long long gaps100 = 0, gaps250 = 0, gaps500 = 0;
        unsigned long long receive10 = 0, receive50 = 0, receive100 = 0;
        unsigned long long samples = 0, blackSamples = 0, unchangedSamples = 0;
        unsigned long long contentChanges = 0, unchangedStreak = 0, maxUnchangedStreak = 0;
        unsigned long long nonBlackRuns100 = 0, nonBlackRuns250 = 0, nonBlackRuns500 = 0;
        unsigned long long readback10 = 0, readback50 = 0, readback100 = 0;
        double maxGapMs = 0.0;
        double maxReceiveMs = 0.0;
        double maxReadbackMs = 0.0;
        double maxUnchangedMs = 0.0;
        double currentRunUnchangedMs = 0.0;
        double maxNonBlackUnchangedMs = 0.0;
        long long maxNonBlackUnchangedQpc = 0;
        std::uint64_t lastHash = 0;
        bool haveHash = false;
        bool currentRunBlack = false;
        long long lastContentChangeQpc = 0;
        const double cpuStarted = CpuSeconds();
        auto finalizeContentRun = [&]() {
            if (!haveHash || currentRunBlack) return;
            if (currentRunUnchangedMs >= 100.0) ++nonBlackRuns100;
            if (currentRunUnchangedMs >= 250.0) ++nonBlackRuns250;
            if (currentRunUnchangedMs >= 500.0) ++nonBlackRuns500;
        };

        stage = "receive-loop";
        while (!stopRequested) {
            long long before = Qpc();
            double elapsed = (before - processStarted) / static_cast<double>(frequency);
            if (elapsed >= options.durationSeconds) break;
            if (GetFileAttributesA(options.stopFile.c_str()) != INVALID_FILE_ATTRIBUTES) break;

            bool received = receiver.ReceiveTexture();
            long long after = Qpc();
            ++polls;
            double receiveMs = (after - before) * 1000.0 / frequency;
            maxReceiveMs = std::max(maxReceiveMs, receiveMs);
            if (receiveMs >= 10.0) ++receive10;
            if (receiveMs >= 50.0) ++receive50;
            if (receiveMs >= 100.0) ++receive100;
            if (receiveMs >= 10.0) {
                log << "{\"event\":\"slow-receive\",\"qpc\":" << after
                    << ",\"receiveMs\":" << receiveMs << "}\n";
            }

            bool connected = received && receiver.IsConnected();
            if (!connected) {
                ++receiveFailures;
                if (wasConnected) {
                    ++disconnects;
                    log << "{\"event\":\"disconnect\",\"qpc\":" << after << "}\n";
                    lastFrameQpc = 0;
                }
                wasConnected = false;
            }
            else {
                const char* actualName = receiver.GetSenderName();
                if (!actualName || options.sender != actualName)
                    throw std::runtime_error("Connected sender name differs from requested sender");

                ID3D11Texture2D* texture = receiver.GetSenderTexture();
                if (!texture) throw std::runtime_error("Connected receiver has no private texture");
                D3D11_TEXTURE2D_DESC description{};
                texture->GetDesc(&description);
                HANDLE currentHandle = receiver.GetSenderHandle();
                DWORD currentFormat = static_cast<DWORD>(receiver.GetSenderFormat());
                if (!wasConnected) {
                    connectedEver = true;
                    log << "{\"event\":\"connect\",\"qpc\":" << after
                        << ",\"width\":" << description.Width
                        << ",\"height\":" << description.Height
                        << ",\"format\":" << currentFormat << "}\n";
                }
                if (width != 0 && (width != description.Width || height != description.Height ||
                                   format != currentFormat || shareHandle != currentHandle)) {
                    ++metadataChanges;
                    log << "{\"event\":\"metadata-change\",\"qpc\":" << after
                        << ",\"width\":" << description.Width
                        << ",\"height\":" << description.Height
                        << ",\"format\":" << currentFormat << "}\n";
                    lastFrameQpc = 0;
                }
                width = description.Width;
                height = description.Height;
                format = currentFormat;
                shareHandle = currentHandle;
                wasConnected = true;

                long counter = receiver.GetSenderFrame();
                bool reportsNew = receiver.IsFrameNew();
                if (counter > 0) frameCounterAvailable = true;
                if (counter > 0 && counter != lastCounter) {
                    long delta = lastCounter > 0 ? counter - lastCounter : 1;
                    double gapMs = lastFrameQpc > 0
                        ? (after - lastFrameQpc) * 1000.0 / frequency : 0.0;
                    if (delta <= 0) {
                        ++counterResets;
                        delta = 1;
                        gapMs = 0.0;
                    }
                    else if (lastFrameQpc > 0) {
                        ++observedIntervals;
                        maxGapMs = std::max(maxGapMs, gapMs);
                        if (gapMs >= 100.0) ++gaps100;
                        if (gapMs >= 250.0) ++gaps250;
                        if (gapMs >= 500.0) ++gaps500;
                        if (delta > 1) {
                            ++counterJumps;
                            missedSenderFrames += static_cast<unsigned long long>(delta - 1);
                        }
                    }
                    ++uniqueFrames;
                    log << "{\"event\":\"frame\",\"qpc\":" << after
                        << ",\"senderFrame\":" << counter
                        << ",\"delta\":" << delta
                        << ",\"gapMs\":" << gapMs
                        << ",\"receiveMs\":" << receiveMs
                        << ",\"reportsNew\":" << reportsNew << "}\n";
                    lastCounter = counter;
                    lastFrameQpc = after;

                    if (lastSampleQpc == 0 ||
                        (after - lastSampleQpc) * 1000.0 / frequency >= options.sampleMilliseconds) {
                        PixelSample pixel = sampler.Capture(device, context, texture, frequency);
                        long long sampledAt = Qpc();
                        if (pixel.available) {
                            ++samples;
                            if (pixel.blackPixels == pixel.sampledPixels) ++blackSamples;
                            bool unchanged = haveHash && pixel.hash == lastHash;
                            double unchangedMs = 0.0;
                            if (unchanged) {
                                ++unchangedSamples;
                                ++unchangedStreak;
                                maxUnchangedStreak = std::max(maxUnchangedStreak, unchangedStreak);
                                if (lastContentChangeQpc > 0)
                                    unchangedMs = (sampledAt - lastContentChangeQpc) * 1000.0 / frequency;
                                maxUnchangedMs = std::max(maxUnchangedMs, unchangedMs);
                                currentRunUnchangedMs = unchangedMs;
                                if (!currentRunBlack && unchangedMs > maxNonBlackUnchangedMs) {
                                    maxNonBlackUnchangedMs = unchangedMs;
                                    maxNonBlackUnchangedQpc = sampledAt;
                                }
                            }
                            else {
                                if (haveHash) {
                                    ++contentChanges;
                                    finalizeContentRun();
                                }
                                unchangedStreak = 0;
                                currentRunUnchangedMs = 0.0;
                                currentRunBlack = pixel.blackPixels == pixel.sampledPixels;
                                lastContentChangeQpc = sampledAt;
                            }
                            maxReadbackMs = std::max(maxReadbackMs, pixel.gpuReadbackMs);
                            if (pixel.gpuReadbackMs >= 10.0) ++readback10;
                            if (pixel.gpuReadbackMs >= 50.0) ++readback50;
                            if (pixel.gpuReadbackMs >= 100.0) ++readback100;
                            std::ostringstream hashText;
                            hashText << std::hex << std::setw(16) << std::setfill('0') << pixel.hash;
                            log << "{\"event\":\"pixel-sample\",\"qpc\":" << sampledAt
                                << ",\"senderFrame\":" << counter
                                << ",\"contentHash\":" << JsonString(hashText.str())
                                << ",\"blackPixels\":" << pixel.blackPixels
                                << ",\"sampledPixels\":" << pixel.sampledPixels
                                << ",\"unchanged\":" << unchanged
                                << ",\"unchangedStreak\":" << unchangedStreak
                                << ",\"unchangedMs\":" << unchangedMs
                                << ",\"gpuReadbackMs\":" << pixel.gpuReadbackMs << "}\n";
                            lastHash = pixel.hash;
                            haveHash = true;
                        }
                        else {
                            log << "{\"event\":\"pixel-sample-unavailable\",\"qpc\":"
                                << sampledAt << ",\"format\":" << currentFormat << "}\n";
                        }
                        lastSampleQpc = sampledAt;
                    }
                }
            }

            if (after - lastHeartbeatQpc >= frequency) {
                double sinceLastFrameMs = lastFrameQpc > 0
                    ? (after - lastFrameQpc) * 1000.0 / frequency : -1.0;
                log << "{\"event\":\"heartbeat\",\"qpc\":" << after
                    << ",\"connected\":" << wasConnected
                    << ",\"senderFrame\":" << lastCounter
                    << ",\"uniqueFrames\":" << uniqueFrames
                    << ",\"missedSenderFrames\":" << missedSenderFrames
                    << ",\"sinceLastFrameMs\":" << sinceLastFrameMs
                    << ",\"cpuSeconds\":" << (CpuSeconds() - cpuStarted) << "}\n";
                lastHeartbeatQpc = after;
            }
            if (after - lastFlushQpc >= frequency) {
                log.flush();
                if (!log) throw std::runtime_error("Output write failed");
                lastFlushQpc = after;
            }
            Sleep(static_cast<DWORD>(options.pollMilliseconds));
        }

        stage = "cleanup";
        finalizeContentRun();
        receiver.ReleaseReceiver();
        receiver.CloseDirectX11();
        receiverOwner.reset();
        long long ended = Qpc();
        log << "{\"event\":\"summary\",\"qpc\":" << ended
            << ",\"errors\":0"
            << ",\"wallSeconds\":" << (ended - processStarted) / static_cast<double>(frequency)
            << ",\"cpuSeconds\":" << (CpuSeconds() - cpuStarted)
            << ",\"connectedEver\":" << connectedEver
            << ",\"frameCounterAvailable\":" << frameCounterAvailable
            << ",\"polls\":" << polls
            << ",\"receiveFailures\":" << receiveFailures
            << ",\"uniqueFrames\":" << uniqueFrames
            << ",\"observedIntervals\":" << observedIntervals
            << ",\"counterJumps\":" << counterJumps
            << ",\"missedSenderFrames\":" << missedSenderFrames
            << ",\"counterResets\":" << counterResets
            << ",\"disconnects\":" << disconnects
            << ",\"metadataChanges\":" << metadataChanges
            << ",\"gapsAtLeast100Ms\":" << gaps100
            << ",\"gapsAtLeast250Ms\":" << gaps250
            << ",\"gapsAtLeast500Ms\":" << gaps500
            << ",\"maxGapMs\":" << maxGapMs
            << ",\"receiveAtLeast10Ms\":" << receive10
            << ",\"receiveAtLeast50Ms\":" << receive50
            << ",\"receiveAtLeast100Ms\":" << receive100
            << ",\"maxReceiveMs\":" << maxReceiveMs
            << ",\"pixelSamples\":" << samples
            << ",\"blackPixelSamples\":" << blackSamples
            << ",\"unchangedPixelSamples\":" << unchangedSamples
            << ",\"contentChanges\":" << contentChanges
            << ",\"maxUnchangedSampleStreak\":" << maxUnchangedStreak
            << ",\"maxUnchangedMs\":" << maxUnchangedMs
            << ",\"nonBlackRunsAtLeast100Ms\":" << nonBlackRuns100
            << ",\"nonBlackRunsAtLeast250Ms\":" << nonBlackRuns250
            << ",\"nonBlackRunsAtLeast500Ms\":" << nonBlackRuns500
            << ",\"maxNonBlackUnchangedMs\":" << maxNonBlackUnchangedMs
            << ",\"maxNonBlackUnchangedQpc\":" << maxNonBlackUnchangedQpc
            << ",\"readbackAtLeast10Ms\":" << readback10
            << ",\"readbackAtLeast50Ms\":" << readback50
            << ",\"readbackAtLeast100Ms\":" << readback100
            << ",\"maxGpuReadbackMs\":" << maxReadbackMs
            << ",\"width\":" << width
            << ",\"height\":" << height
            << ",\"format\":" << format << "}\n";
        log.flush();
        return connectedEver && frameCounterAvailable && uniqueFrames >= 2 && log ? 0 : 2;
    }
    catch (const std::exception& error) {
        if (log.is_open()) {
            log << "{\"event\":\"error\",\"qpc\":" << Qpc()
                << ",\"stage\":" << JsonString(stage)
                << ",\"message\":" << JsonString(error.what()) << "}\n";
            log.flush();
        }
        std::cerr << error.what() << '\n';
        return 1;
    }
}
