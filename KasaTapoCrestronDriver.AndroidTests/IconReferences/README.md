# Reviewed Room tile icons

These four 42 × 42 crops were visually reviewed on 26 September 2026 from Crestron Home's Room tiles on a 720 × 1600 Android emulator. They contain only the app's icon artwork, with no device names, household details or credentials. The artwork belongs to Crestron; these small references are included solely for testing its presentation.

| Resource | Reviewed appearance |
| --- | --- |
| `icGenericDeviceOn` | Dark navy four-diamond device symbol |
| `icGenericDeviceOff` | Muted grey four-diamond device symbol |
| `icClimateRegular` | Outlined thermometer |
| `icStarOff` | Grey circle containing a white star |

`TileIconVerification` finds the selected tile's `serviceIcon` bounds in the same retained hierarchy as the screenshot. It compares the crop at its native size, including colour, with a mean RGB-channel error limit of 2/255 and at most 2% of pixels exceeding a 24/255 channel difference. These fixed tolerances allow small antialiasing variation. Tests reject a blank icon, another glyph, the wrong on/off colour and a changed size.

Unknown icons (including unreviewed alert, low-battery and motion-active states) fail explicitly; they are not accepted by substituting another reference. Changed app geometry also requires review. Do not automatically regenerate references from a failing run. Review the source-selected icon and the screenshot first, and retain the failed result when updating an intentional rendering change.

This is a captured-state check. It does not prove sensor stimulation or the timing of an icon transition. The image decoder is a Windows test-only dependency and is not packaged in the driver.
