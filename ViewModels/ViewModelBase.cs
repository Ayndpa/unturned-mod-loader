using CommunityToolkit.Mvvm.ComponentModel;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected ViewModelBase()
    {
        LocalizationService.LanguageChanged += OnLocalizationChanged;
    }

    protected virtual void OnLocalizationChanged()
    {
    }
}