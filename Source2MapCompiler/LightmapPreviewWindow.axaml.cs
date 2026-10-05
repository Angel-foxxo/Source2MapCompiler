using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Source2MapCompiler.LightmapPreview;

namespace Source2MapCompiler;

[SupportedOSPlatform("windows")]
public partial class LightmapPreviewWindow : Window
{
    private LightmapAtlas? atlas;

    public LightmapPreviewWindow()
    {
        InitializeComponent();

        ExposureSlider.ValueChanged += (_, e) => SetExposure((float)e.NewValue);
    }

    public void ShowUpdate(LightmapPreviewUpdate update)
    {
        if (update.IsNewAtlas || atlas != update.Atlas)
        {
            atlas = update.Atlas;
            View.SetAtlas(atlas);
            SaveButton.IsEnabled = true;
        }
        else
        {
            View.Refresh();
        }

        var count = update.Count;
        var size = $"{update.Atlas.Width}x{update.Atlas.Height}";

        Status.Text = update.Stage switch
        {
            LightmapPreviewStage.Waiting => $"Waiting for vrad3 to start baking {count} blocks  |  {size}",
            LightmapPreviewStage.Baking => $"Baking block {update.Index + 1} of {count}  |  {size}  |  before filtering",
            LightmapPreviewStage.BlockDone => $"Block {update.Index + 1} of {count} done  |  {size}  |  before filtering",
            LightmapPreviewStage.ProbeVolume => $"Light probe volume {update.Index + 1} of {count} baked  |  {size}  |  depth slices left to right, one row per direction",
            _ => $"Baked and filtered  |  {size}  |  before compression",
        };
    }

    public void ShowEnded(string message)
    {
        Status.Text = message;
    }

    // In stops from the auto exposure, which shows the lightmap's average brightness as middle grey the way Source 2 Viewer
    // does. The GPU applies it as the lightmap is drawn, so it's instant at any size
    private void SetExposure(float exposure)
    {
        ExposureLabel.Text = exposure.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        View.Exposure = exposure;
    }

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        View.FitToView();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (atlas == null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save lightmap",
            SuggestedFileName = "lightmap.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });

        if (file == null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await View.SavePng(stream);
    }
}
