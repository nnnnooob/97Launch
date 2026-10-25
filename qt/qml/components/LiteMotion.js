.pragma library

// PCL AniEaseOutBack(Weak): 1 - (1 - t)^2 * cos(1.5 * PI * t).
// Convert once to native cubic segments; no JavaScript runs on animation frames.
function backCurve() {
    var curve = [];
    var a = 1.5 * Math.PI;
    function value(t) { return 1 - Math.pow(1 - t, 2) * Math.cos(a * t); }
    function slope(t) { return 2 * (1 - t) * Math.cos(a * t) + a * Math.pow(1 - t, 2) * Math.sin(a * t); }
    for (var i = 0; i < 8; ++i) {
        var start = i / 8;
        var end = (i + 1) / 8;
        curve.push(start + 1 / 24, value(start) + slope(start) / 24,
                   end - 1 / 24, value(end) - slope(end) / 24,
                   end, value(end));
    }
    return curve;
}

var outBackWeak = backCurve();
