/* HAP の BC テクスチャを GPU で BGRA へ展開する（v0.5.0）。
 *
 * 置き場所の考え方: リングもフェンスも触らない。ここが作るのは「BGRA のテクスチャ 1 枚」で、
 * 呼び出し側は既存の copy_ring_locked にそのまま渡す。**壊れる範囲をこのファイルの中に収める。**
 *
 * 流れ: 展開済みの BC データ → BC テクスチャへ転送（UpdateSubresource）→ 全画面クアッドで
 * BGRA のテクスチャへ描く（Hap Q は Scaled YCoCg を RGB に戻す）。
 * シェーダは変換済みのバイト列を埋め込んである（tcs_hap_shaders.h。実行時の d3dcompiler 依存なし）。
 *
 * スレッド: 呼び出しは 1 本（GStreamer のストリーミングスレッド、frame_lock 保持中）を想定する。
 * D3D11 のデバイスコンテキストは shim の他の描画と同じものを使うので、同時に触らせない。
 */
#ifndef TCS_HAP_GPU_H
#define TCS_HAP_GPU_H

#include <d3d11.h>
#include <stdint.h>

#include "tcs_hap.h"

typedef struct TcsHapGpu TcsHapGpu;

/* 作る。device と context は shim のもの（所有はしない）。失敗したら nullptr。 */
TcsHapGpu* tcs_hap_gpu_create (ID3D11Device* device, ID3D11DeviceContext* context);
void tcs_hap_gpu_destroy (TcsHapGpu* gpu);

/* 展開済みの BC データ 1 コマを BGRA のテクスチャへ描く。
 * 返すのは内部が持つテクスチャ（呼び出し側は Release しない。次の呼び出しで上書きされる）。
 * 失敗したら nullptr。 */
ID3D11Texture2D* tcs_hap_gpu_decode (TcsHapGpu* gpu, const uint8_t* bc_data, size_t bc_size,
                                     int texture_format, int width, int height);

/* 直近の失敗の理由（ログ用。無ければ空文字）。 */
const char* tcs_hap_gpu_last_error (const TcsHapGpu* gpu);

#endif /* TCS_HAP_GPU_H */
