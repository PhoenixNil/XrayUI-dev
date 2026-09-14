using XrayUI.Services;
using XrayUI.ViewModels;

namespace XrayUI.Tests;

public class ConfigProfileViewModelTests
{
    private readonly SettingsService _settings = new();
    private readonly ConfigProfileStore _profiles = new();
    private readonly ProfileTestDialogs _dialogs = new();

    private ConfigProfileViewModel Create(bool tunSlot)
    {
        var vm = new ConfigProfileViewModel(_settings, _profiles, _dialogs, () => Task.FromResult<string?>(null));
        vm.SetInitialSlot(tunSlot);
        return vm;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialLoad_UsesTheRequestedSlot(bool tunSlot)
    {
        var requestedSlots = new List<bool>();
        _profiles.Read = slot => { requestedSlots.Add(slot); return Task.FromResult<string?>(null); };
        var vm = Create(tunSlot);

        Assert.False(vm.CanEdit);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.LoadAsync();

        Assert.Equal(new[] { tunSlot }, requestedSlots);
        Assert.Equal(tunSlot, vm.IsTunSlot);
        Assert.Equal(tunSlot ? XrayConfigBuilder.TunTemplate : XrayConfigBuilder.ProxyTemplate, vm.EditorText);
        Assert.True(vm.CanEdit);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task InitialRead_CannotBeOvertakenByASelectionChange()
    {
        var pending = new TaskCompletionSource<string?>();
        _profiles.Read = _ => pending.Task;
        var vm = Create(true);
        var load = vm.LoadAsync();

        await vm.SwitchSlotAsync(0);
        Assert.False(vm.SaveCommand.CanExecute(null));
        pending.SetResult(XrayConfigBuilder.TunTemplate);
        await load;

        Assert.True(vm.IsTunSlot);
        Assert.Equal(XrayConfigBuilder.TunTemplate, vm.EditorText);
    }

    [Fact]
    public async Task Switch_CommitsSelectionContentAndEnabledFlagTogether()
    {
        _settings.Settings.UseTunConfigProfile = true;
        var vm = Create(false);
        await vm.LoadAsync();
        var pending = new TaskCompletionSource<string?>();
        _profiles.Read = _ => pending.Task;
        var switching = vm.SwitchSlotAsync(1);

        Assert.False(vm.IsTunSlot);
        Assert.Equal(XrayConfigBuilder.ProxyTemplate, vm.EditorText);
        Assert.False(vm.CanEdit);
        pending.SetResult(XrayConfigBuilder.TunTemplate);
        await switching;

        Assert.True(vm.IsTunSlot);
        Assert.True(vm.IsProfileEnabled);
        Assert.Equal(XrayConfigBuilder.TunTemplate, vm.EditorText);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task CancelSwitch_KeepsTheOriginalSlotAndUnsavedText()
    {
        var vm = Create(true);
        await vm.LoadAsync();
        vm.EditorText += " ";
        var draft = vm.EditorText;
        var pending = new TaskCompletionSource<bool>();
        _dialogs.Confirm = () => pending.Task;
        var switching = vm.SwitchSlotAsync(0);

        Assert.True(vm.IsTunSlot);
        pending.SetResult(false);
        await switching;

        Assert.True(vm.IsTunSlot);
        Assert.Equal(draft, vm.EditorText);
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task Save_CannotBeRedirectedToAnotherSlotWhileWriting()
    {
        var vm = Create(true);
        await vm.LoadAsync();
        vm.IsProfileEnabled = true;
        var pending = new TaskCompletionSource();
        bool? writtenSlot = null;
        _profiles.Write = (slot, _) => { writtenSlot = slot; return pending.Task; };
        var saving = vm.SaveCommand.ExecuteAsync(null);

        await vm.SwitchSlotAsync(0);
        Assert.False(vm.CanEdit);
        pending.SetResult();
        await saving;

        Assert.Equal(true, writtenSlot);
        Assert.True(vm.IsTunSlot);
        Assert.True(_settings.Settings.UseTunConfigProfile);
        Assert.False(_settings.Settings.UseProxyConfigProfile);
    }

    [Fact]
    public async Task FailedRead_ReleasesBusyStateAndDoesNotShowThePreviousSlotText()
    {
        var vm = Create(false);
        await vm.LoadAsync();
        _profiles.Read = _ => throw new IOException("Test failure");

        await vm.SwitchSlotAsync(1);

        Assert.True(vm.IsTunSlot);
        Assert.Empty(vm.EditorText);
        Assert.True(vm.IsValidationOpen);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanEdit);
    }
}
