using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace TimecodeSyncPlayer.Output;

/// <summary>Gpu 出力に必要な D3D11.4（ID3D11Device5 / ID3D11DeviceContext4 / 共有フェンス）の検出結果。</summary>
internal readonly record struct D3D11CapabilityResult(bool Supported, string Detail);

/// <summary>
/// 起動時に D3D11.4 の可否を検出する。Gpu 出力は ID3D11Device5 /
/// ID3D11DeviceContext4 / 共有フェンス（NT ハンドル）がすべて使える場合だけ成立する。
/// </summary>
internal static class D3D11CapabilityProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static D3D11CapabilityResult Detect()
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0],
                out device,
                out context).CheckError();

            if (device is null || context is null)
                return new(false, "D3D11 デバイスを作成できませんでした。");

            using ID3D11Device5? device5 = device.QueryInterfaceOrNull<ID3D11Device5>();
            if (device5 is null)
                return new(false, "ID3D11Device5 を取得できません（D3D11.4 非対応）。");

            using ID3D11DeviceContext4? context4 = context.QueryInterfaceOrNull<ID3D11DeviceContext4>();
            if (context4 is null)
                return new(false, "ID3D11DeviceContext4 を取得できません（D3D11.4 非対応）。");

            using ID3D11Fence fence = device5.CreateFence(0, FenceFlags.Shared);
            IntPtr handle = fence.CreateSharedHandle(null, null!);
            try
            {
                if (handle == IntPtr.Zero)
                    return new(false, "共有フェンスの NT ハンドルを作成できませんでした。");
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }

            return new(true, "D3D11.4 共有フェンス利用可。");
        }
        catch (Exception ex)
        {
            return new(false, $"D3D11.4 の検出に失敗しました: {ex.Message}");
        }
        finally
        {
            context?.ClearState();
            context?.Flush();
            context?.Dispose();
            device?.Dispose();
        }
    }
}
