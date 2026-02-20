import sys

path = sys.argv[1]
with open(path, "r") as f:
    content = f.read()

old = """    IntersectionResult result;
    result.segment = best->score.segment;
    result.multiplier = best->score.multiplier;
    result.score = best->score.score;
    result.method = method;
    result.confidence = confidence;
    result.coords = best->coords;
    result.total_error = best->total_error;
    for (const auto& [cam_id, data] : cam_lines)
        result.per_camera[cam_id] = data.vote;
    
    return result;
}"""

new = """    IntersectionResult result;
    result.segment = best->score.segment;
    result.multiplier = best->score.multiplier;
    result.score = best->score.score;
    result.method = method;
    result.confidence = confidence;
    result.coords = best->coords;
    result.total_error = best->total_error;
    for (const auto& [cam_id, data] : cam_lines)
        result.per_camera[cam_id] = data.vote;
    
    // Simple multiplier voting across cameras
    // If 2+ cameras agree on a multiplier and it differs from intersection result, override
    {
        int mult_votes[4] = {0}; // index 1=single, 2=double, 3=triple
        for (const auto& [cam_id, data] : cam_lines) {
            int m = data.vote.multiplier;
            if (m >= 1 && m <= 3) mult_votes[m]++;
        }
        int majority_mult = result.multiplier;
        int max_votes = 0;
        for (int m = 1; m <= 3; m++) {
            if (mult_votes[m] > max_votes) {
                max_votes = mult_votes[m];
                majority_mult = m;
            }
        }
        if (max_votes >= 2 && majority_mult != result.multiplier) {
            result.multiplier = majority_mult;
            // Recalculate score based on new multiplier
            if (result.segment == 25) {
                result.score = (result.multiplier == 2) ? 50 : 25;
            } else {
                result.score = result.segment * result.multiplier;
            }
        }
    }
    
    return result;
}"""

if old in content:
    content = content.replace(old, new)
    with open(path, "w") as f:
        f.write(content)
    print("PATCHED OK")
else:
    print("OLD TEXT NOT FOUND")
