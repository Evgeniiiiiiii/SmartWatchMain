# SmartWatchProj agent rules

## Scope
- Work only inside this repository.
- Do not touch files outside this repo.
- Do not modify build artifacts, archives, publish output, or generated binaries unless explicitly asked.

## Safety
- Before making changes, briefly explain the plan.
- Keep diffs minimal and scoped to the task.
- Prefer small, reversible edits.
- Do not delete files unless explicitly requested.
- Do not rename major modules unless explicitly requested.

## Validation
- After changes, run available build or test checks when possible.
- Report what changed, what was checked, and any remaining risks.

## Git workflow
- Never merge directly to main.
- Work in the current branch only.
- Prepare changes for user review before integration.

## Project specifics
- Preserve current app behavior unless the task requires changes.
- Treat ONNX/YOLO inference, camera logic, and UI bindings as high-risk areas.
- Do not change model paths, asset loading, or packet/data formats without explicit approval.
