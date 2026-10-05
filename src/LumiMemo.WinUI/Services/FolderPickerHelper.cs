using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LumiMemo.WinUI.Services;

/// <summary>选文件夹的公用入口（首次运行向导与设置页共用）。</summary>
/// <remarks>
/// <para>
/// 免打包（unpackaged）应用里，<see cref="FolderPicker"/> 必须先绑上窗口句柄才能弹，
/// 否则调用当场抛 COM 异常。这个绑定没有别的地方会做，两处调用点收在这一个入口里。
/// </para>
/// <para>
/// 用经典的 <c>Windows.Storage.Pickers</c> 而不是 Windows App SDK 2.x 新出的
/// <c>Microsoft.Windows.Storage.Pickers</c>：后者更新，但经典这套在 Win10 1809
/// （本程序支持的最低版本）上同样可用，是风险最小的一条。
/// </para>
/// </remarks>
public static class FolderPickerHelper
{
    /// <summary>弹文件夹选择器；用户取消时返回 <see langword="null"/>。</summary>
    /// <param name="owner">宿主窗口，用来取 HWND。</param>
    public static async Task<string?> PickFolderAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };

        // 过滤器必须非空，空集合会让调用直接抛异常；选文件夹时这个值不参与筛选。
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));

        StorageFolder? folder = await picker.PickSingleFolderAsync();

        return folder?.Path;
    }
}
