# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.0.10] - 2026-08-21

### Fixed

- Applied opaque atmospheric scattering before volumetric clouds so compatible cloud renderers can fog and scatter clouds at cloud depth without projecting opaque geometry silhouettes through them.


## [1.0.9] - 2026-08-16

### Added

- Added a shared physically based sky ambient-probe evaluator so static consumers can cache the same spherical-harmonics lighting used by dynamic mode.


## [1.0.8] - 2026-08-15

### Fixed

- Fixed block-shaped sun disk artifacts on Android by retaining full precision for celestial and atmospheric view calculations.
- Fixed static PBR skies replacing baked ambient lighting with stale or dark probe data during scene reloads and build startup.


## [1.0.7] - 2026-08-09

### Fixed

- Fixed black skies after cold editor starts and platform switches by compiling required LUT shader passes before one-time precomputation.
- Fixed high-quality precomputation on Unity 6 Render Graph by removing texture feedback loops and declaring the required LUT write-to-read transitions.
- Fixed global LUT dependencies for atmospheric scattering and ambient probe rendering.

### Known Issues

- Compatibility Mode does not work.


## [1.0.6] - 2026-08-07

### Added

- Added Burst compiler support for atmospheric optical depth calculations to improve performance.

### Fixed

- Fixed Unity 6.4 API changes for ScriptableRenderPass.
- Fixed texture access flags in ExecutePass for Unity 6.3+ where Compatibility Mode is removed; LUTs and other textures are now declared as ReadWrite to align with stricter Render Graph requirements.

### Known Issues

- Compatibility Mode does not work.


## [1.0.5] - 2026-07-25

### Fixed

- Fixed HDR decoding for the space emission cubemap.


## [1.0.4] - 2025-05-17

### Added

- Added support for "`cn.unity.physical-light-unit`" (external URP package).

### Fixed

- Fixed a rendering issue with dynamic resolution enabled.


## [1.0.3] - 2025-04-02

### Fixed

- Fixed a null reference issue when entering prefab mode in the editor.


## [1.0.2] - 2025-04-01

### Fixed

- Fixed an issue where the sun attenuation incorrectly treated the alpha channel as the blue channel.


## [1.0.1] - 2025-03-27

### Changed

- Adjusted the URP package requirement from 14.0.11 (Unity 2022.3) to 14.0.7 (Unity 2022.2).


## [1.0.0] - 2025-03-06

### Added

- Initial release of this package.
