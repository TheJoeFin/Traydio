using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// The notification-area icon's GUID is what lets Windows 11 keep the icon promoted to the
/// taskbar across an update that moves the exe. Changing the packaged value would orphan every
/// user's saved placement, so it is pinned here.
/// </summary>
[TestClass]
public sealed class TrayIconIdentityPolicyTests
{
    [TestMethod]
    public void PackagedIconGuid_IsPinned()
    {
        Assert.AreEqual(new Guid("f290d6ce-783f-41ac-8b14-b1edfb468c2d"), TrayIconIdentityPolicy.PackagedIconGuid);
    }

    [TestMethod]
    public void Choose_Packaged_UsesTheConstantRegardlessOfPath()
    {
        Guid a = TrayIconIdentityPolicy.Choose(true, @"C:\Program Files\WindowsApps\Trdo_1.0.0.0_x64__abc\Trdo.exe");
        Guid b = TrayIconIdentityPolicy.Choose(true, @"C:\Program Files\WindowsApps\Trdo_1.0.1.0_x64__abc\Trdo.exe");

        Assert.AreEqual(TrayIconIdentityPolicy.PackagedIconGuid, a);
        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Choose_Unpackaged_DoesNotCollideWithThePackagedApp()
    {
        Guid guid = TrayIconIdentityPolicy.Choose(false, @"D:\source\Trdo\bin\Trdo.exe");

        Assert.AreNotEqual(TrayIconIdentityPolicy.PackagedIconGuid, guid);
    }

    [TestMethod]
    public void Choose_Unpackaged_IsStablePerPathAndIgnoresCase()
    {
        Guid first = TrayIconIdentityPolicy.Choose(false, @"D:\source\Trdo\bin\Trdo.exe");
        Guid again = TrayIconIdentityPolicy.Choose(false, @"d:\SOURCE\trdo\bin\trdo.EXE");

        Assert.AreEqual(first, again);
    }

    [TestMethod]
    public void Choose_Unpackaged_DifferentPathsGetDifferentGuids()
    {
        Guid a = TrayIconIdentityPolicy.Choose(false, @"D:\a\Trdo.exe");
        Guid b = TrayIconIdentityPolicy.Choose(false, @"D:\b\Trdo.exe");

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void Choose_Unpackaged_WithoutAPath_FallsBackToTheConstant()
    {
        Assert.AreEqual(TrayIconIdentityPolicy.PackagedIconGuid, TrayIconIdentityPolicy.Choose(false, null));
        Assert.AreEqual(TrayIconIdentityPolicy.PackagedIconGuid, TrayIconIdentityPolicy.Choose(false, "  "));
    }
}
