using System;
using System.IO;
using WorkPilot.Host.Core.Scheduling;
using Xunit;

namespace WorkPilot.Host.Core.Tests;

public class ExecutablePathValidatorTests
{
    [Fact]
    public void Valid_host_exe_within_root_passes()
    {
        var root = CreateRoot(out var exe);
        var result = new ExecutablePathValidator().Validate(exe, root);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Relative_path_is_rejected()
    {
        var root = CreateRoot(out _);
        var result = new ExecutablePathValidator().Validate("WorkPilot.Host.exe", root);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Missing_file_is_rejected()
    {
        var root = CreateEmptyRoot();
        var result = new ExecutablePathValidator().Validate(Path.Combine(root, "WorkPilot.Host.exe"), root);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Path_escaping_root_is_rejected()
    {
        var root = CreateRoot(out _);
        var outside = Path.Combine(Path.GetTempPath(), "wp_outside_" + Guid.NewGuid().ToString("N"), "WorkPilot.Host.exe");
        var result = new ExecutablePathValidator().Validate(outside, root);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Wrong_exe_name_is_rejected()
    {
        var root = CreateRoot(out _, "notthehost.exe");
        var result = new ExecutablePathValidator().Validate(Path.Combine(root, "notthehost.exe"), root);
        Assert.False(result.IsSuccess);
    }

    private static string CreateRoot(out string exe, string exeName = "WorkPilot.Host.exe")
    {
        var root = Path.Combine(Path.GetTempPath(), "wp_host_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        exe = Path.Combine(root, exeName);
        File.WriteAllText(exe, "");
        return root;
    }

    private static string CreateEmptyRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp_host_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
