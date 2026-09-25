"""Write an SMPTE LTC signal (biphase mark, 80 bits/frame) to a WAV file.

Used by scripts/make-heavy-media.ps1 to put LTC on the first audio channel of a
test clip (reproduces LTC leaking from the media audio into the input). The
second channel carries a quiet 1 kHz tone so the clip is a normal stereo track.

  python scripts/make-ltc-wav.py OUT.wav --fps 30 --seconds 20 [--rate 48000] [--start 0]

Non-drop-frame only (24/25/30). Stdlib only.
"""
import argparse
import math
import struct
import wave

SYNC_WORD = [0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1]


def frame_bits(hours, minutes, seconds, frames, fps):
    bits = [0] * 80

    def put(value, start, count):
        for i in range(count):
            bits[start + i] = (value >> i) & 1

    put(frames % 10, 0, 4)
    put(frames // 10, 8, 2)
    put(seconds % 10, 16, 4)
    put(seconds // 10, 24, 3)
    put(minutes % 10, 32, 4)
    put(minutes // 10, 40, 3)
    put(hours % 10, 48, 4)
    put(hours // 10, 56, 2)
    bits[64:80] = SYNC_WORD
    # Polarity correction bit (bit 27 at 25 fps, bit 59 otherwise): even number of ones.
    parity_bit = 27 if fps == 25 else 59
    if sum(bits) % 2 == 1:
        bits[parity_bit] = 1
    return bits


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('out')
    parser.add_argument('--fps', type=int, default=30, choices=[24, 25, 30])
    parser.add_argument('--seconds', type=float, default=20.0)
    parser.add_argument('--rate', type=int, default=48000)
    parser.add_argument('--start', type=int, default=0, help='start frame number')
    args = parser.parse_args()

    total_frames = int(round(args.seconds * args.fps))
    samples_per_bit = args.rate / (args.fps * 80.0)
    level = 0.5
    ltc = []
    phase = 1.0
    position = 0.0
    emitted = 0
    for n in range(total_frames):
        frame_number = args.start + n
        frames = frame_number % args.fps
        total_seconds = frame_number // args.fps
        bits = frame_bits((total_seconds // 3600) % 24, (total_seconds // 60) % 60, total_seconds % 60, frames, args.fps)
        for bit in bits:
            # Biphase mark: flip at every bit boundary, and mid-bit for a 1.
            phase = -phase
            half = position + samples_per_bit / 2.0
            end = position + samples_per_bit
            while emitted < int(half):
                ltc.append(phase * level)
                emitted += 1
            if bit:
                phase = -phase
            while emitted < int(end):
                ltc.append(phase * level)
                emitted += 1
            position = end

    with wave.open(args.out, 'wb') as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(args.rate)
        frames_out = bytearray()
        for i, value in enumerate(ltc):
            tone = 0.05 * math.sin(2.0 * math.pi * 1000.0 * i / args.rate)
            frames_out += struct.pack('<hh', int(value * 32767), int(tone * 32767))
        w.writeframes(bytes(frames_out))
    print('wrote %s: %d frames at %d fps, %d samples' % (args.out, total_frames, args.fps, len(ltc)))


if __name__ == '__main__':
    main()
