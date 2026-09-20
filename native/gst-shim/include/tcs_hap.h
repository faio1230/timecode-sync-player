/* HAP のフレーム解析と Snappy 展開（純粋なデータ処理。D3D11 も GStreamer も使わない）。
 *
 * HAP は「BC 圧縮したテクスチャを、さらに Snappy で縮めて mov に入れた」形式。
 * 1 コマの中身:
 *   セクションヘッダ 4 バイト（長さ 3 + 種別 1。長さ 0 なら 8 バイト形式で、続く 4 バイトが長さ）
 *   種別の下位ニブル = テクスチャ形式（0x0B=DXT1 / 0x0E=DXT5 / 0x0F=Scaled YCoCg DXT5 ほか）
 *   種別の上位ニブル = 二段目の圧縮（0xA=無圧縮 / 0xB=Snappy / 0xC=かたまり分割）
 * 仕様: https://github.com/Vidvox/hap/blob/master/documentation/HapVideoDRAFT.md
 *
 * かたまり分割（0xC）は、1 コマを複数に割って**並列に展開できる**ようにした形。先頭に
 * 「展開の手順」セクション（種別 0x01）が入り、その中にかたまりごとの圧縮方式・サイズが並ぶ。
 */
#ifndef TCS_HAP_H
#define TCS_HAP_H

#include <stddef.h>
#include <stdint.h>
#include <string.h>

/* テクスチャ形式（種別の下位ニブル）。 */
enum {
  TCS_HAP_FORMAT_RGB_DXT1 = 0x0B,
  TCS_HAP_FORMAT_RGBA_DXT5 = 0x0E,
  TCS_HAP_FORMAT_YCOCG_DXT5 = 0x0F,
  TCS_HAP_FORMAT_RGTC1 = 0x01,
  TCS_HAP_FORMAT_RGBA_BPTC = 0x0C,
  TCS_HAP_FORMAT_RGB_BPTC_FLOAT = 0x0D,
};

/* 二段目の圧縮（種別の上位ニブル）。 */
enum {
  TCS_HAP_COMPRESSOR_NONE = 0xA,
  TCS_HAP_COMPRESSOR_SNAPPY = 0xB,
  TCS_HAP_COMPRESSOR_COMPLEX = 0xC,
};

/* 1 コマに入れられるかたまりの上限。実素材は 1〜16 程度（CPU のコア数に合わせて作られる）。 */
#define TCS_HAP_MAX_CHUNKS 64

typedef struct TcsHapChunk {
  int compressor;           /* TCS_HAP_COMPRESSOR_NONE / _SNAPPY */
  uint32_t offset;          /* コマの先頭からのバイト位置 */
  uint32_t length;          /* 圧縮された大きさ */
  uint32_t output_offset;   /* 展開先の先頭からのバイト位置 */
  uint32_t output_length;   /* 展開後の大きさ */
} TcsHapChunk;

typedef struct TcsHapFrameInfo {
  int texture_format;                     /* TCS_HAP_FORMAT_* */
  uint32_t decompressed_length;           /* 展開後の合計（= BC データの大きさ） */
  int chunk_count;
  TcsHapChunk chunks[TCS_HAP_MAX_CHUNKS];
} TcsHapFrameInfo;

/* ---------------- Snappy（展開のみ） ----------------
 *
 * 生のブロック形式（フレーミング無し）。先頭に展開後の長さの varint、その後はタグの並び:
 *   下位 2 bit が 00 = リテラル、01/10/11 = コピー（それぞれ 1/2/4 バイトのオフセット）
 * コピーは重なってよい（1 バイトずつ写す意味）。**入力は信用しない**ので、すべて境界を確かめる。
 */

/* 展開後の長さだけを読む。壊れていれば 0 を返す。 */
static inline uint32_t
tcs_snappy_uncompressed_length (const uint8_t* src, size_t src_len)
{
  uint32_t value = 0;
  int shift = 0;
  for (size_t i = 0; i < src_len && shift <= 28; i++) {
    uint8_t byte = src[i];
    value |= (uint32_t) (byte & 0x7F) << shift;
    if ((byte & 0x80) == 0)
      return value;
    shift += 7;
  }
  return 0;
}

/* 展開する。書けた長さを written に返す。範囲を 1 バイトでも超えるなら 0 を返して何も保証しない。 */
static inline int
tcs_snappy_decompress (const uint8_t* src, size_t src_len, uint8_t* dst, size_t dst_cap,
                       size_t* written)
{
  if (!src || !dst || !written)
    return 0;
  *written = 0;

  /* 展開後の長さ（varint）を読み飛ばす。 */
  size_t cursor = 0;
  uint32_t expected = 0;
  int shift = 0;
  for (;;) {
    if (cursor >= src_len || shift > 28)
      return 0;
    uint8_t byte = src[cursor++];
    expected |= (uint32_t) (byte & 0x7F) << shift;
    if ((byte & 0x80) == 0)
      break;
    shift += 7;
  }
  if (expected > dst_cap)
    return 0;

  size_t out = 0;
  while (cursor < src_len) {
    uint8_t tag = src[cursor++];
    int type = tag & 0x03;
    if (type == 0) {
      /* リテラル。長さ-1 が上位 6 bit、60 以上なら続く 1〜4 バイトが長さ-1（リトルエンディアン）。 */
      uint32_t length = (uint32_t) (tag >> 2);
      if (length >= 60) {
        uint32_t extra = length - 59;   /* 1〜4 バイト */
        if (cursor + extra > src_len)
          return 0;
        uint32_t value = 0;
        for (uint32_t i = 0; i < extra; i++)
          value |= (uint32_t) src[cursor + i] << (8 * i);
        cursor += extra;
        length = value;
      }
      length += 1;
      if (cursor + length > src_len || out + length > expected)
        return 0;
      memcpy (dst + out, src + cursor, length);
      cursor += length;
      out += length;
      continue;
    }

    uint32_t length = 0, offset = 0;
    if (type == 1) {
      if (cursor >= src_len)
        return 0;
      length = 4 + (uint32_t) ((tag >> 2) & 0x07);
      offset = (uint32_t) ((tag >> 5) & 0x07) << 8 | src[cursor++];
    } else if (type == 2) {
      if (cursor + 2 > src_len)
        return 0;
      length = 1 + (uint32_t) (tag >> 2);
      offset = (uint32_t) src[cursor] | ((uint32_t) src[cursor + 1] << 8);
      cursor += 2;
    } else {
      if (cursor + 4 > src_len)
        return 0;
      length = 1 + (uint32_t) (tag >> 2);
      offset = (uint32_t) src[cursor] | ((uint32_t) src[cursor + 1] << 8)
          | ((uint32_t) src[cursor + 2] << 16) | ((uint32_t) src[cursor + 3] << 24);
      cursor += 4;
    }
    if (offset == 0 || offset > out || out + length > expected)
      return 0;
    /* 重なるコピーがあるので 1 バイトずつ写す（memcpy では意味が変わる）。 */
    size_t from = out - offset;
    for (uint32_t i = 0; i < length; i++)
      dst[out + i] = dst[from + i];
    out += length;
  }
  if (out != expected)
    return 0;
  *written = out;
  return 1;
}

/* ---------------- HAP のフレーム解析 ---------------- */

/* セクションヘッダを読む。読めたら 1 を返し、長さ・種別・ヘッダ自身の大きさを返す。 */
static inline int
tcs_hap_read_section (const uint8_t* data, size_t size, size_t offset,
                      uint32_t* out_length, int* out_type, uint32_t* out_header)
{
  if (offset + 4 > size)
    return 0;
  uint32_t length = (uint32_t) data[offset] | ((uint32_t) data[offset + 1] << 8)
      | ((uint32_t) data[offset + 2] << 16);
  int type = data[offset + 3];
  if (length != 0) {
    *out_length = length;
    *out_type = type;
    *out_header = 4;
    return 1;
  }
  if (offset + 8 > size)
    return 0;
  *out_length = (uint32_t) data[offset + 4] | ((uint32_t) data[offset + 5] << 8)
      | ((uint32_t) data[offset + 6] << 16) | ((uint32_t) data[offset + 7] << 24);
  *out_type = type;
  *out_header = 8;
  return 1;
}

/* 1 コマを解析する。扱えない形なら 0。 */
static inline int
tcs_hap_parse (const uint8_t* data, size_t size, TcsHapFrameInfo* info)
{
  if (!data || !info || size < 4)
    return 0;
  memset (info, 0, sizeof (*info));

  uint32_t section_length = 0, header = 0;
  int section_type = 0;
  if (!tcs_hap_read_section (data, size, 0, &section_length, &section_type, &header))
    return 0;
  int compressor = (section_type & 0xF0) >> 4;
  info->texture_format = section_type & 0x0F;
  size_t body = header;
  if (body + section_length > size)
    return 0;

  if (compressor == TCS_HAP_COMPRESSOR_NONE || compressor == TCS_HAP_COMPRESSOR_SNAPPY) {
    uint32_t decompressed = compressor == TCS_HAP_COMPRESSOR_NONE
        ? section_length
        : tcs_snappy_uncompressed_length (data + body, section_length);
    if (decompressed == 0)
      return 0;
    info->decompressed_length = decompressed;
    info->chunk_count = 1;
    info->chunks[0].compressor = compressor;
    info->chunks[0].offset = (uint32_t) body;
    info->chunks[0].length = section_length;
    info->chunks[0].output_offset = 0;
    info->chunks[0].output_length = decompressed;
    return 1;
  }
  if (compressor != TCS_HAP_COMPRESSOR_COMPLEX)
    return 0;

  /* かたまり分割。先頭の「展開の手順」セクション（種別 0x01）から表を読む。 */
  uint32_t instructions_length = 0, instructions_header = 0;
  int instructions_type = 0;
  if (!tcs_hap_read_section (data, size, body, &instructions_length, &instructions_type,
          &instructions_header))
    return 0;
  if ((instructions_type & 0x0F) != 0x01)
    return 0;
  size_t cursor = body + instructions_header;
  size_t instructions_end = cursor + instructions_length;
  if (instructions_end > size)
    return 0;

  const uint8_t* compressors = nullptr;
  const uint8_t* sizes = nullptr;
  const uint8_t* uncompressed_sizes = nullptr;
  int count = 0;
  while (cursor < instructions_end) {
    uint32_t length = 0, entry_header = 0;
    int type = 0;
    if (!tcs_hap_read_section (data, size, cursor, &length, &type, &entry_header))
      return 0;
    size_t payload = cursor + entry_header;
    if (payload + length > size)
      return 0;
    switch (type & 0x0F) {
      case 0x02:   /* かたまりごとの二段目圧縮（1 バイトずつ） */
        compressors = data + payload;
        count = (int) length;
        break;
      case 0x03:   /* かたまりごとの圧縮後サイズ（4 バイトずつ） */
        sizes = data + payload;
        break;
      case 0x05:   /* かたまりごとの展開後サイズ（4 バイトずつ） */
        uncompressed_sizes = data + payload;
        break;
      default:
        break;     /* 0x04（オフセット表）は使わない。サイズから積む */
    }
    cursor = payload + length;
  }
  if (!compressors || !sizes || count <= 0 || count > TCS_HAP_MAX_CHUNKS)
    return 0;

  size_t data_offset = instructions_end;
  uint32_t output_offset = 0;
  for (int i = 0; i < count; i++) {
    uint32_t compressed_size = (uint32_t) sizes[i * 4] | ((uint32_t) sizes[i * 4 + 1] << 8)
        | ((uint32_t) sizes[i * 4 + 2] << 16) | ((uint32_t) sizes[i * 4 + 3] << 24);
    if (data_offset + compressed_size > size)
      return 0;
    uint32_t uncompressed;
    if (uncompressed_sizes) {
      uncompressed = (uint32_t) uncompressed_sizes[i * 4]
          | ((uint32_t) uncompressed_sizes[i * 4 + 1] << 8)
          | ((uint32_t) uncompressed_sizes[i * 4 + 2] << 16)
          | ((uint32_t) uncompressed_sizes[i * 4 + 3] << 24);
    } else if (compressors[i] == TCS_HAP_COMPRESSOR_SNAPPY) {
      uncompressed = tcs_snappy_uncompressed_length (data + data_offset, compressed_size);
    } else {
      uncompressed = compressed_size;
    }
    if (uncompressed == 0)
      return 0;
    info->chunks[i].compressor = compressors[i];
    info->chunks[i].offset = (uint32_t) data_offset;
    info->chunks[i].length = compressed_size;
    info->chunks[i].output_offset = output_offset;
    info->chunks[i].output_length = uncompressed;
    data_offset += compressed_size;
    output_offset += uncompressed;
  }
  info->chunk_count = count;
  info->decompressed_length = output_offset;
  return 1;
}

/* 解析済みのコマを展開する。かたまりは出力の範囲が重ならないので、呼び出し側で並列にしてよい。 */
static inline int
tcs_hap_decompress_chunk (const uint8_t* data, size_t size, const TcsHapChunk* chunk,
                          uint8_t* dst, size_t dst_cap)
{
  if (!data || !chunk || !dst)
    return 0;
  if ((size_t) chunk->offset + chunk->length > size)
    return 0;
  if ((size_t) chunk->output_offset + chunk->output_length > dst_cap)
    return 0;
  if (chunk->compressor == TCS_HAP_COMPRESSOR_NONE) {
    if (chunk->length != chunk->output_length)
      return 0;
    memcpy (dst + chunk->output_offset, data + chunk->offset, chunk->length);
    return 1;
  }
  if (chunk->compressor != TCS_HAP_COMPRESSOR_SNAPPY)
    return 0;
  size_t written = 0;
  if (!tcs_snappy_decompress (data + chunk->offset, chunk->length, dst + chunk->output_offset,
          chunk->output_length, &written))
    return 0;
  return written == chunk->output_length;
}

/* 1 コマぶんを順に展開する（並列にしたい場合は呼び出し側で chunk ごとに回す）。 */
static inline int
tcs_hap_decompress_frame (const uint8_t* data, size_t size, const TcsHapFrameInfo* info,
                          uint8_t* dst, size_t dst_cap)
{
  if (!info || info->chunk_count <= 0)
    return 0;
  if (info->decompressed_length > dst_cap)
    return 0;
  for (int i = 0; i < info->chunk_count; i++) {
    if (!tcs_hap_decompress_chunk (data, size, &info->chunks[i], dst, dst_cap))
      return 0;
  }
  return 1;
}

/* BC のブロックあたりのバイト数（DXT1 と RGTC1 は 8、ほかは 16）。 */
static inline int
tcs_hap_block_bytes (int texture_format)
{
  return (texture_format == TCS_HAP_FORMAT_RGB_DXT1 || texture_format == TCS_HAP_FORMAT_RGTC1)
      ? 8 : 16;
}

/* 幅・高さから、展開後に期待される大きさ（バイト）。0 なら扱えない形。 */
static inline uint32_t
tcs_hap_expected_size (int texture_format, int width, int height)
{
  if (width <= 0 || height <= 0)
    return 0;
  int blocks_x = (width + 3) / 4;
  int blocks_y = (height + 3) / 4;
  return (uint32_t) blocks_x * (uint32_t) blocks_y * (uint32_t) tcs_hap_block_bytes (texture_format);
}

#endif /* TCS_HAP_H */
