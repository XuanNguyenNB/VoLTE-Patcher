namespace VoLTEVendorPatcher;

/// <summary>
/// Describes one vendor generation in a single place. Supporting a new,
/// verified generation only requires a new template and a catalog entry.
/// </summary>
internal sealed record PatchProfileDefinition(
    PatchProfile Id,
    int MinFirstApi,
    int MaxFirstApi,
    string InitTemplate,
    string InitSha256,
    IReadOnlyList<string> RequiredInitLines)
{
    public bool Supports(int firstApi) => firstApi >= MinFirstApi && firstApi <= MaxFirstApi;
}

internal static class PatchProfileCatalog
{
    private static readonly IReadOnlyList<PatchProfileDefinition> Profiles =
    [
        new(
            PatchProfile.LegacyApi27,
            27,
            27,
            "legacy.rc",
            "C9DF5D3D0ADFF8CC2A3A4EBB4CAF7A6B060BC98D7773BC3EBCE960DDA408CCE4",
            [
                "setprop persist.mtk.ims_support 1",
                "setprop persist.mtk.volte_support 1",
                "setprop persist.mtk.volte.enable 1",
                "setprop persist.radio.volte_state 1",
                "setprop persist.dbg.volte_avail_ovr 1",
                "setprop persist.dbg.vt_avail_ovr 1"
            ]),
        new(
            PatchProfile.ModernApi28Plus,
            28,
            29,
            "modern.rc",
            "4CC704105EF8BCBEE685D44FF9B0B126B8568B396D4CDB9AD94026F45BA790EF",
            [
                "setprop persist.mtk.ims_support 1",
                "setprop persist.mtk.volte_support 1",
                "setprop persist.mtk.volte.enable 1",
                "setprop persist.radio.volte_state 1",
                "setprop persist.dbg.volte_avail_ovr 1",
                "setprop persist.dbg.vt_avail_ovr 1",
                "setprop persist.vendor.mtk.ims_support 1",
                "setprop persist.vendor.mtk.volte_support 1",
                "setprop persist.vendor.mtk.volte.enable 3",
                "setprop persist.vendor.radio.volte_state 3",
                "setprop persist.vendor.mtk_dynamic_ims_switch 0"
            ])
    ];

    public static PatchProfileDefinition? Resolve(int firstApi) =>
        Profiles.SingleOrDefault(profile => profile.Supports(firstApi));

    public static PatchProfileDefinition Get(PatchProfile id) =>
        Profiles.Single(profile => profile.Id == id);

    public static string SupportedApiSummary => string.Join(", ", Profiles.Select(profile =>
        profile.MinFirstApi == profile.MaxFirstApi
            ? profile.MinFirstApi.ToString()
            : $"{profile.MinFirstApi}–{profile.MaxFirstApi}"));
}
