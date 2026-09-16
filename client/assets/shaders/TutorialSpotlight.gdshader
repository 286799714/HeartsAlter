shader_type canvas_item;
render_mode unshaded;

uniform vec4 shade_color : source_color = vec4(0.23, 0.23, 0.23, 0.72);
uniform vec4 outline_color : source_color = vec4(1.0, 0.85, 0.4, 1.0);
uniform vec2 overlay_size = vec2(1280.0, 720.0);
uniform vec4 focus_rects[16];
uniform int focus_count = 0;
uniform float outline_width = 3.0;
uniform float corner_radius = 6.0;

void fragment() {
	vec2 point = UV * overlay_size;
	float nearest = 100000.0;
	for (int i = 0; i < 16; i++) {
		if (i >= focus_count) { break; }
		vec4 rect = focus_rects[i];
		vec2 half_size = rect.zw * 0.5;
		float radius = min(corner_radius, min(half_size.x, half_size.y));
		vec2 delta = abs(point - rect.xy - half_size) - half_size + vec2(radius);
		float distance_to_rect = length(max(delta, vec2(0.0))) + min(max(delta.x, delta.y), 0.0) - radius;
		nearest = min(nearest, distance_to_rect);
	}
	if (nearest < 0.0) {
		COLOR = vec4(0.0);
	} else if (nearest < outline_width) {
		COLOR = outline_color;
	} else {
		COLOR = shade_color;
	}
}
