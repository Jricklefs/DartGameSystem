"""WHRS Weight Optimization via Coordinate Descent"""
import json, time, urllib.request, urllib.error, sys

API = "http://localhost:5000"

def set_flag(name, value):
    req = urllib.request.Request(f"{API}/api/benchmark/set-flag?name={name}&value={value}", method="POST")
    urllib.request.urlopen(req, timeout=10)

def replay():
    req = urllib.request.Request(f"{API}/api/benchmark/replay?includeDetails=true", method="POST")
    resp = urllib.request.urlopen(req, timeout=600)
    return json.loads(resp.read())

def set_weights(weights):
    """weights = dict of name->float, e.g. wR=0.30. API takes int (value/100)."""
    for k, v in weights.items():
        set_flag(f"WHRS_{k}", int(round(v * 100)))

def run_with_weights(weights):
    set_weights(weights)
    time.sleep(1)
    result = replay()
    return result["correct"], result["totalDarts"], result["accuracyPct"]

# Enable all required flags
print("Setting base flags...")
time.sleep(15)  # wait for API startup
set_flag("UseIQDL", 1)
set_flag("UseBarrelConfidenceWeightedTriangulation", 1)
set_flag("UseBCWTRadialStabilityClamp", 1)
set_flag("UseHHS", 1)
set_flag("UseWHRS", 1)

# Phase 26 defaults
defaults = {"wR": 0.30, "wI": 0.15, "wA": 0.20, "wQ": 0.10, "wB": 0.10, "wD": 0.10, "wC": 0.05}

# First run baseline with defaults
print(f"\n=== Baseline (Phase 26 defaults) ===")
correct, total, pct = run_with_weights(defaults)
print(f"Baseline: {correct}/{total} = {pct}%")
best_weights = dict(defaults)
best_correct = correct
best_pct = pct

results = [{"weights": dict(defaults), "correct": correct, "pct": pct, "label": "baseline"}]

# Coordinate descent: optimize one weight at a time
weight_names = ["wR", "wI", "wA", "wQ", "wB", "wD", "wC"]
search_multipliers = [0.5, 0.75, 1.25, 1.5, 2.0]

for round_num in range(2):  # 2 rounds of coordinate descent
    print(f"\n=== Round {round_num + 1} ===")
    for wname in weight_names:
        base_val = best_weights[wname]
        print(f"\nOptimizing {wname} (current={base_val:.3f})")
        best_for_dim = best_correct
        best_val_for_dim = base_val
        
        for mult in search_multipliers:
            test_val = base_val * mult
            if test_val < 0.01 or test_val > 0.80:
                continue
            test_weights = dict(best_weights)
            test_weights[wname] = test_val
            
            correct, total, pct = run_with_weights(test_weights)
            label = f"R{round_num+1}_{wname}={test_val:.3f}"
            results.append({"weights": dict(test_weights), "correct": correct, "pct": pct, "label": label})
            print(f"  {wname}={test_val:.3f}: {correct}/{total} = {pct}% {'***' if correct > best_for_dim else ''}")
            
            if correct > best_for_dim:
                best_for_dim = correct
                best_val_for_dim = test_val
        
        if best_val_for_dim != base_val:
            best_weights[wname] = best_val_for_dim
            best_correct = best_for_dim
            print(f"  >> Updated {wname}: {base_val:.3f} -> {best_val_for_dim:.3f}")

# Final validation with best weights
print(f"\n=== Final validation ===")
correct, total, pct = run_with_weights(best_weights)
print(f"Optimized: {correct}/{total} = {pct}%")
best_pct = pct

# Save results
output = {
    "phase26_defaults": defaults,
    "optimized_weights": best_weights,
    "baseline_correct": results[0]["correct"],
    "baseline_pct": results[0]["pct"],
    "optimized_correct": correct,
    "optimized_pct": pct,
    "delta_correct": correct - results[0]["correct"],
    "delta_pct": round(pct - results[0]["pct"], 2),
    "all_runs": results
}

with open(r"C:\Users\clawd\DartGameSystem\debug_outputs\phase27_optimization_results.json", "w") as f:
    json.dump(output, f, indent=2)
print(f"\nSaved results. Best weights: {best_weights}")
print(f"Improvement: {correct - results[0]['correct']} darts ({results[0]['pct']}% -> {pct}%)")
