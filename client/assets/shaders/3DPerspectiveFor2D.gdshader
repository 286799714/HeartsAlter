/// Faux 3D Perspective Shader for Control / TextureRect.
///
/// Based on CodeVogel's Faux 3D Perspective shader,
/// modified for TextureRect whose display size may differ
/// from the source texture size.

shader_type canvas_item;

uniform bool cull_backface = true;

uniform float fov : hint_range(1.0, 120.0) = 60.0;
uniform float rot_y_deg : hint_range(-180.0, 180.0) = 0.0;
uniform float rot_x_deg : hint_range(-180.0, 180.0) = 0.0;

/// Actual TextureRect size in Control-space pixels.
/// Must be updated from C# when the Control resizes.
uniform vec2 rect_size = vec2(100.0, 140.0);

/// 0 = reserve maximum geometry space for perspective.
/// Higher values reduce the extra geometry.
uniform float inset : hint_range(0.0, 1.0) = 0.0;

/// Soft outline generated outside the card by its rounded-rectangle SDF.
uniform bool outline_enabled = true;
uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 0.8);
/// Width in displayed pixels. The vertex stage expands the drawable area by
/// this amount so the outline can extend beyond a full-bleed texture.
uniform float outline_width : hint_range(0.0, 12.0, 0.1) = 4.0;
/// Higher values keep the outline closer to the card edge.
uniform float outline_fade_power : hint_range(0.1, 4.0, 0.1) = 1.3;
/// Rounded corner radius in displayed pixels.
uniform float card_corner_radius : hint_range(0.0, 64.0, 0.1) = 8.0;

varying vec2 perspective_offset;
varying vec3 world_pos_3d;
varying vec4 vertex_modulate;


void vertex()
{
    vertex_modulate = COLOR;

    // Expand the projection UV range beyond 0..1. The expanded quad gives
    // the SDF an actual rasterized area outside the opaque card edge.
    vec2 projection_uv = UV;
    if (outline_enabled && outline_width > 0.0)
    {
        vec2 safe_rect_size = max(rect_size, vec2(1.0));
        projection_uv =
            (UV - vec2(0.5))
            * (vec2(1.0) + 2.0 * outline_width / safe_rect_size)
            + vec2(0.5);
    }

    float rot_y = radians(rot_y_deg);
    float rot_x = radians(rot_x_deg);

    float sin_y = sin(rot_y);
    float cos_y = cos(rot_y);

    float sin_x = sin(rot_x);
    float cos_x = cos(rot_x);

    mat3 rotation_matrix;

    rotation_matrix[0] =
        vec3(cos_y, 0.0, -sin_y);

    rotation_matrix[1] =
        vec3(
            sin_y * sin_x,
            cos_x,
            cos_y * sin_x
        );

    rotation_matrix[2] =
        vec3(
            sin_y * cos_x,
            -sin_x,
            cos_y * cos_x
        );

    float perspective_scale =
        max(tan(radians(fov) * 0.5), 0.001);

    // Convert normalized card coordinates into pseudo-3D.
    world_pos_3d =
        rotation_matrix *
        vec3(
            projection_uv - vec2(0.5),
            0.5 / perspective_scale
        );

    float depth_scale =
        (0.5 / perspective_scale) + 0.5;

    world_pos_3d.xy *=
        depth_scale * rotation_matrix[2].z;

    perspective_offset =
        depth_scale * rotation_matrix[2].xy;

    // IMPORTANT:
    //
    // Original shader uses:
    //
    // (UV - 0.5) / TEXTURE_PIXEL_SIZE
    //
    // which means "source texture dimensions".
    //
    // TextureRect may display the source texture at an
    // entirely different size, so use rect_size instead.
    VERTEX +=
        (projection_uv - vec2(0.5))
        * rect_size
        * perspective_scale
        * (1.0 - inset);
}


void fragment()
{
    float z = world_pos_3d.z;

    if (abs(z) < 0.0001)
        discard;

    if (cull_backface && z <= 0.0)
        discard;

    vec2 projected_uv =
        (world_pos_3d.xy / z)
        - perspective_offset
        + vec2(0.5);

    // Evaluate the rounded rectangle in displayed pixels so outline_width
    // and card_corner_radius remain stable when the card is resized.
    vec2 shape_size = max(rect_size, vec2(1.0));
    vec2 shape_point = (projected_uv - vec2(0.5)) * shape_size;
    vec2 half_size = shape_size * 0.5;
    float radius = min(card_corner_radius, min(half_size.x, half_size.y));
    vec2 rounded_box = abs(shape_point) - half_size + radius;
    float signed_distance =
        length(max(rounded_box, vec2(0.0)))
        + min(max(rounded_box.x, rounded_box.y), 0.0)
        - radius;

    float sdf_width = max(fwidth(signed_distance), 0.001);
    // Near an edge-on rotation, the projection's z = 0 line can make
    // derivatives enormous far outside the card. Keep antialiasing inside
    // the silhouette so those fragments cannot become a detached white line.
    float shape_alpha = 1.0 - smoothstep(-sdf_width, 0.0, signed_distance);

    float outline_fade = 0.0;
    if (outline_enabled && outline_width > 0.0 && outline_color.a > 0.0)
    {
        // Outside the card, alpha fades from the SDF edge to transparent.
        // Antialias inward here too: the derivative must never extend the
        // outline beyond its finite width at the projection's singularity.
        outline_fade = 1.0 - smoothstep(
            -sdf_width,
            outline_width,
            signed_distance
        );
        outline_fade = pow(
            outline_fade,
            max(outline_fade_power, 0.001)
        );
    }

    // Completely outside both the card and its outline.
    if (shape_alpha <= 0.0 && outline_fade <= 0.0)
        discard;

    vec2 safe_uv = clamp(projected_uv, vec2(0.0), vec2(1.0));
    // The projective UV is reconstructed in the fragment stage, so provide
    // its true derivatives explicitly. This keeps texture filtering smooth
    // on rotated cards instead of relying on the pre-projective interpolant.
    vec4 tex = textureGrad(
        TEXTURE,
        safe_uv,
        dFdx(projected_uv),
        dFdy(projected_uv)
    );

    float texture_alpha = tex.a * shape_alpha;
    float base_alpha = texture_alpha * vertex_modulate.a;
    float border_alpha =
        outline_fade
        * outline_color.a
        * vertex_modulate.a;
    float result_alpha = base_alpha + border_alpha * (1.0 - base_alpha);

    // The outline is behind the opaque card, matching an external glow/border
    // while preserving the source texture's colors inside the SDF shape.
    vec3 base_rgb = tex.rgb * vertex_modulate.rgb;
    vec3 border_rgb = outline_color.rgb * vertex_modulate.rgb;
    vec3 result_rgb =
        base_rgb * base_alpha
        + border_rgb * border_alpha * (1.0 - base_alpha);

    COLOR = vec4(result_rgb / max(result_alpha, 0.00001), result_alpha);
}
