# Review: AGY Spark heatmap cell

## Scores

- Philosophy: 5/5
- Hierarchy: 5/5
- Execution: 4/5
- Specificity: 5/5
- Restraint: 4/5

## Keep

- `spark-filled.png` retains the original `spark.svg` artwork and fills the four transparent corners with the corresponding adjacent hues.
- The 256 px render has the same rounded-corner proportion as the existing heatmap cells and remains legible at 15 px.

## Fix

- [P2] The boundary between the SVG silhouette and the added corner colours is visible when magnified. It is negligible at heatmap size; if the tile is reused at icon size, refine the underlying corner gradient.

## Quick wins

- Regenerate the PNG from `design/artifacts/spark-corners/tile.html` whenever the original SVG changes.

## Next refine direction

`fidelity`
