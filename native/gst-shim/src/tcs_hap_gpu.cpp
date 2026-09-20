/* tcs_hap_gpu.h の実装。詳しい考え方はヘッダの先頭を参照。 */
#include "tcs_hap_gpu.h"
#include "tcs_hap_shaders.h"

#include <stdio.h>
#include <string.h>

struct TcsHapGpu {
  ID3D11Device* device = nullptr;              /* 借り物（Release しない） */
  ID3D11DeviceContext* context = nullptr;      /* 借り物 */
  ID3D11VertexShader* vs = nullptr;
  ID3D11PixelShader* ps_plain = nullptr;
  ID3D11PixelShader* ps_ycocg = nullptr;
  ID3D11SamplerState* sampler = nullptr;
  /* 全画面クアッドは頂点バッファ無しで作るため、面の向きが裏になりうる。既定のままだと
   * 何も描かれない（試作でこれに当たった）。向きを見ない設定にしておく。 */
  ID3D11RasterizerState* rasterizer = nullptr;

  /* 素材に合わせて作り直す（幅・高さ・形式が変わったとき）。 */
  ID3D11Texture2D* source = nullptr;           /* BC（圧縮）テクスチャ */
  ID3D11ShaderResourceView* source_view = nullptr;
  ID3D11Texture2D* output = nullptr;           /* BGRA。呼び出し側がリングへコピーする */
  ID3D11RenderTargetView* output_view = nullptr;
  int width = 0, height = 0, format = 0;

  char last_error[256] = {};
};

static void
set_error (TcsHapGpu* gpu, const char* fmt, ...)
{
  if (!gpu)
    return;
  va_list args;
  va_start (args, fmt);
  vsnprintf (gpu->last_error, sizeof (gpu->last_error), fmt, args);
  va_end (args);
}

static void
release_textures (TcsHapGpu* gpu)
{
  if (gpu->source_view) { gpu->source_view->Release (); gpu->source_view = nullptr; }
  if (gpu->source) { gpu->source->Release (); gpu->source = nullptr; }
  if (gpu->output_view) { gpu->output_view->Release (); gpu->output_view = nullptr; }
  if (gpu->output) { gpu->output->Release (); gpu->output = nullptr; }
  gpu->width = gpu->height = gpu->format = 0;
}

static DXGI_FORMAT
dxgi_format_for (int texture_format)
{
  switch (texture_format) {
    case TCS_HAP_FORMAT_RGB_DXT1: return DXGI_FORMAT_BC1_UNORM;
    case TCS_HAP_FORMAT_RGBA_DXT5:
    case TCS_HAP_FORMAT_YCOCG_DXT5: return DXGI_FORMAT_BC3_UNORM;
    case TCS_HAP_FORMAT_RGTC1: return DXGI_FORMAT_BC4_UNORM;
    case TCS_HAP_FORMAT_RGBA_BPTC: return DXGI_FORMAT_BC7_UNORM;
    case TCS_HAP_FORMAT_RGB_BPTC_FLOAT: return DXGI_FORMAT_BC6H_UF16;
    default: return DXGI_FORMAT_UNKNOWN;
  }
}

TcsHapGpu*
tcs_hap_gpu_create (ID3D11Device* device, ID3D11DeviceContext* context)
{
  if (!device || !context)
    return nullptr;
  TcsHapGpu* gpu = new TcsHapGpu ();
  gpu->device = device;
  gpu->context = context;

  if (FAILED (device->CreateVertexShader (kTcsHapVertexShader, sizeof (kTcsHapVertexShader),
          nullptr, &gpu->vs))
      || FAILED (device->CreatePixelShader (kTcsHapPixelShaderPlain,
          sizeof (kTcsHapPixelShaderPlain), nullptr, &gpu->ps_plain))
      || FAILED (device->CreatePixelShader (kTcsHapPixelShaderYCoCg,
          sizeof (kTcsHapPixelShaderYCoCg), nullptr, &gpu->ps_ycocg))) {
    tcs_hap_gpu_destroy (gpu);
    return nullptr;
  }

  D3D11_SAMPLER_DESC sampler = {};
  sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
  sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
  sampler.MaxLOD = D3D11_FLOAT32_MAX;
  if (FAILED (device->CreateSamplerState (&sampler, &gpu->sampler))) {
    tcs_hap_gpu_destroy (gpu);
    return nullptr;
  }

  D3D11_RASTERIZER_DESC raster = {};
  raster.FillMode = D3D11_FILL_SOLID;
  raster.CullMode = D3D11_CULL_NONE;
  raster.DepthClipEnable = TRUE;
  if (FAILED (device->CreateRasterizerState (&raster, &gpu->rasterizer))) {
    tcs_hap_gpu_destroy (gpu);
    return nullptr;
  }
  return gpu;
}

void
tcs_hap_gpu_destroy (TcsHapGpu* gpu)
{
  if (!gpu)
    return;
  release_textures (gpu);
  if (gpu->rasterizer) gpu->rasterizer->Release ();
  if (gpu->sampler) gpu->sampler->Release ();
  if (gpu->ps_ycocg) gpu->ps_ycocg->Release ();
  if (gpu->ps_plain) gpu->ps_plain->Release ();
  if (gpu->vs) gpu->vs->Release ();
  delete gpu;
}

static bool
ensure_textures (TcsHapGpu* gpu, int texture_format, int width, int height)
{
  if (gpu->source && gpu->output && gpu->width == width && gpu->height == height
      && gpu->format == texture_format)
    return true;
  release_textures (gpu);

  DXGI_FORMAT format = dxgi_format_for (texture_format);
  if (format == DXGI_FORMAT_UNKNOWN) {
    set_error (gpu, "unsupported hap texture format 0x%02X", texture_format);
    return false;
  }

  D3D11_TEXTURE2D_DESC desc = {};
  desc.Width = (UINT) width;
  desc.Height = (UINT) height;
  desc.MipLevels = 1;
  desc.ArraySize = 1;
  desc.Format = format;
  desc.SampleDesc.Count = 1;
  desc.Usage = D3D11_USAGE_DEFAULT;
  desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
  if (FAILED (gpu->device->CreateTexture2D (&desc, nullptr, &gpu->source))
      || FAILED (gpu->device->CreateShaderResourceView (gpu->source, nullptr, &gpu->source_view))) {
    set_error (gpu, "compressed texture create failed %dx%d format=0x%02X", width, height,
        texture_format);
    release_textures (gpu);
    return false;
  }

  D3D11_TEXTURE2D_DESC out_desc = desc;
  out_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
  out_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
  if (FAILED (gpu->device->CreateTexture2D (&out_desc, nullptr, &gpu->output))
      || FAILED (gpu->device->CreateRenderTargetView (gpu->output, nullptr, &gpu->output_view))) {
    set_error (gpu, "output texture create failed %dx%d", width, height);
    release_textures (gpu);
    return false;
  }

  gpu->width = width;
  gpu->height = height;
  gpu->format = texture_format;
  gpu->last_error[0] = '\0';
  return true;
}

ID3D11Texture2D*
tcs_hap_gpu_decode (TcsHapGpu* gpu, const uint8_t* bc_data, size_t bc_size, int texture_format,
                    int width, int height)
{
  if (!gpu || !bc_data || width <= 0 || height <= 0)
    return nullptr;
  uint32_t expected = tcs_hap_expected_size (texture_format, width, height);
  if (expected == 0 || bc_size < expected) {
    set_error (gpu, "compressed size %zu is smaller than the expected %u (%dx%d)", bc_size,
        expected, width, height);
    return nullptr;
  }
  if (!ensure_textures (gpu, texture_format, width, height))
    return nullptr;

  const UINT row_pitch = (UINT) (((width + 3) / 4) * tcs_hap_block_bytes (texture_format));
  gpu->context->UpdateSubresource (gpu->source, 0, nullptr, bc_data, row_pitch, 0);

  D3D11_VIEWPORT viewport = {};
  viewport.Width = (FLOAT) width;
  viewport.Height = (FLOAT) height;
  viewport.MaxDepth = 1.0f;
  gpu->context->OMSetRenderTargets (1, &gpu->output_view, nullptr);
  gpu->context->RSSetViewports (1, &viewport);
  gpu->context->RSSetState (gpu->rasterizer);
  gpu->context->IASetPrimitiveTopology (D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
  gpu->context->IASetInputLayout (nullptr);
  gpu->context->VSSetShader (gpu->vs, nullptr, 0);
  gpu->context->PSSetShader (
      texture_format == TCS_HAP_FORMAT_YCOCG_DXT5 ? gpu->ps_ycocg : gpu->ps_plain, nullptr, 0);
  gpu->context->PSSetShaderResources (0, 1, &gpu->source_view);
  gpu->context->PSSetSamplers (0, 1, &gpu->sampler);
  gpu->context->Draw (4, 0);

  /* 描画対象を外しておく（呼び出し側がこのテクスチャを読むため）。 */
  ID3D11RenderTargetView* none = nullptr;
  gpu->context->OMSetRenderTargets (1, &none, nullptr);
  gpu->last_error[0] = '\0';
  return gpu->output;
}

const char*
tcs_hap_gpu_last_error (const TcsHapGpu* gpu)
{
  return gpu ? gpu->last_error : "";
}
