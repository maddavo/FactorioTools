# FactorioTools Aquilo Oil-Field Planner

## Objective

Extend the existing FactorioTools oil-field planner with an optional Aquilo heat-pipe mode. Preserve the original planner interface and its normal blueprint-planning features; do not replace it with a simplified tool.

The finished application must remain a static browser application hosted on GitHub Pages. It must not require a separate backend, test server, account, or paid hosting service for normal use.

## Aquilo mode

When the user selects **Add heat pipes**:

- The generated blueprint includes one orthogonally connected heat-pipe network.
- The network has a free exterior connection point for a player-added heating tower or reactor.
- Every Aquilo entity that can freeze is directly edge-adjacent to that network. Diagonal-only contact is not valid.
- The network is mandatory. It must not be deleted or treated as a best-effort post-processing result.

Heat is a first-class constraint while placing and orienting pumpjacks, routing fluid pipes, placing beacons, and placing electric poles or substations. A candidate that prevents a valid heated layout must be moved, rejected, or replaced before it is committed.

The heat backbone may move while alternatives are evaluated. The planner may choose a less dense beacon or power layout when that is necessary for a valid complete result, but it must not silently remove selected categories of generated components to make a failed plan appear valid.

## Planning behaviour

The planner must evaluate complete layouts rather than fixing all non-heat entities and then trying to insert heat pipes afterwards:

1. Select heat-compatible pumpjack orientations and fluid-pipe routing.
2. Plan a connected, externally reachable heat backbone.
3. Add beacons, poles, and substations only when the complete layout remains heat-valid; reroute the heat backbone when beneficial.
4. Rank the valid complete layouts using the existing planner criteria, such as beacon coverage and pipe count.

The implementation is not required to find the mathematical global optimum. It must reliably produce a valid complete layout when one is available within its supported search, and it must not call a layout impossible merely because one route or placement order failed.

## Existing behaviour

- Heat mode is off by default, leaving existing non-Aquilo behaviour unchanged.
- Existing blueprint input/output, beacon planning, electric-pole planning, and other original controls remain available.

## Acceptance criteria

Completion requires all of the following:

1. Existing non-Aquilo regression tests pass.
2. Automated Aquilo tests cover pumpjacks and fluid pipes, beacons, electric poles, substations, an exterior heat connection, and the reported failing layout.
3. Those tests verify direct heat adjacency, one connected heat network, and that heat mode does not silently discard requested generated component categories.
4. The planner core and browser application build successfully.
5. The relevant regression suite completes successfully without hanging or unbounded search.
6. Changes are committed and pushed to `maddavo/FactorioTools`.
7. GitHub Pages is deployed from that revision and the public site is verified with the original planner UI and Aquilo option.

## Delivery

Deliver source changes, regression tests, the pushed commit, the verified GitHub Pages URL, and a concise release note describing the option and any supported-layout limits.
