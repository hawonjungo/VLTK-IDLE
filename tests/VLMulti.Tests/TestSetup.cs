using System.Runtime.CompilerServices;

namespace VLMulti.Tests;

internal static class TestSetup
{
    // Chạy một lần trước mọi test: không ghi log ra file data/vlmulti.log thật
    [ModuleInitializer]
    internal static void Init() => VLMulti.Core.Log.Enabled = false;
}
