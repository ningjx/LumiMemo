// S3 TSF 覆盖实测：只验证 CsWin32 能否投影 TSF 接口，不做任何运行时调用。
// 编译过 = 覆盖；编译错 = 不覆盖（错误信息会列出哪些符号缺失）。

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.TextServices;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 验证 ITextStoreACP2 是否带 GeneratedComInterface（可直接被 .NET 类实现并经
// ComWrappers 暴露给 COM）；打印其 GUID 与自定义 attribute。
var t = typeof(ITextStoreACP2);
Console.WriteLine($"ITextStoreACP2 GUID: {t.GUID}");
Console.WriteLine($"InterfaceType: {t.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name.Contains("InterfaceType"))?.ConstructorArguments.FirstOrDefault().Value}");
foreach (var a in t.GetCustomAttributesData())
{
    Console.WriteLine($"  attr: {a.AttributeType.FullName}");
}
