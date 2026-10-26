{
  description = "Quant Workbench: .NET and Node toolchain (nix-managed), the same pins as ex-grid's flake";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  # Node from a later nixpkgs than the .NET SDK's: the first with Node 26.12.1, the security release
  # of 2026-10-21 (CVE-2026-111497 and others), which .node-version names for CI. Its own input, so
  # moving Node on does not move the SDK with it.
  inputs.nixpkgs-node = {
    url = "https://releases.nixos.org/nixos/unstable/nixos-26.11pre1100280.3ec636362e18/nixexprs.tar.xz";
    flake = false;
  };

  outputs = { self, nixpkgs, nixpkgs-node }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f system (import nixpkgs { inherit system; }));
    in
    {
      devShells = forAllSystems (system: pkgs:
        let
          nodejs = (import nixpkgs-node { inherit system; }).nodejs_26;
          # SDK 10, whose bundled runtime is also the shipped packages' target
          # (ADR-0022), so the gating tests execute on it without a second
          # runtime alongside. (Until 2026-09-25 the target was net8.0 and the
          # net8 runtime was combined in here.)
          dotnet = pkgs.dotnetCorePackages.combinePackages [
            pkgs.dotnet-sdk_10
          ];
        in
        {
        # Node too: ExGrid's build bundles the Script Core's module from src/npm
        # (ADR-0350 section 7), so `dotnet build` runs it.
        default = pkgs.mkShell {
          packages = [ dotnet nodejs ];
          shellHook = ''
            # Child processes (the test host) resolve frameworks from DOTNET_ROOT;
            # without it they follow the muxer's real path into the SDK-only store
            # path and miss the combined runtime.
            export DOTNET_ROOT=${dotnet}/share/dotnet
            export DOTNET_CLI_TELEMETRY_OPTOUT=1
            export DOTNET_NOLOGO=1
            echo "dotnet $(dotnet --version) (nix-managed)"
          '';
        };

        # The layer-3 (browser) tests: Node for Playwright, and dotnet because
        # Playwright starts the DemoHost itself.
        #
        # No browser. `channel: 'chrome'` resolves Google Chrome by its own
        # well-known paths and would never pick up a nix store one, so putting
        # a browser here would be both unused and misleading — nixpkgs'
        # `chromium` is not what that channel means, and `google-chrome` is
        # unfree, which would make this shell fail to build for anyone who has
        # not opted in. Chrome is a machine prerequisite, as
        # tests/ExGrid.Browser/README.md says (ADR-0026).
        #
        # It also used to list `pkgs.chromium` unconditionally, which has no
        # aarch64-darwin build: this shell failed to EVALUATE on an Apple
        # Silicon Mac, so `.#browser` had never once been usable on the machine
        # this project is developed on.
        browser = pkgs.mkShell {
          packages = [ dotnet nodejs ];
          shellHook = ''
            export DOTNET_ROOT=${dotnet}/share/dotnet
            export DOTNET_CLI_TELEMETRY_OPTOUT=1
            export DOTNET_NOLOGO=1
            echo "node $(node --version), dotnet $(dotnet --version)"
          '';
        };
      });
    };
}
