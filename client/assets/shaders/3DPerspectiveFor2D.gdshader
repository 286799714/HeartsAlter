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

varying vec2 perspective_offset;
varying vec3 world_pos_3d;
varying vec4 vertex_modulate;


void vertex()
{
    vertex_modulate = COLOR;

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
            UV - vec2(0.5),
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
        (UV - vec2(0.5))
        * rect_size
        * perspective_scale
        * (1.0 - inset);
}


void fragment()
{
    float z = world_pos_3d.z;

    // Avoid unstable division near 90 degrees.
    if (abs(z) < 0.0001)
        discard;

    if (cull_backface && z <= 0.0)
        discard;

    vec2 projected_uv =
        (world_pos_3d.xy / z)
        - perspective_offset
        + vec2(0.5);

    if (
        projected_uv.x < 0.0 ||
        projected_uv.x > 1.0 ||
        projected_uv.y < 0.0 ||
        projected_uv.y > 1.0
    )
    {
        discard;
    }

    // Preserve Control.Modulate / SelfModulate.
    COLOR =
        texture(TEXTURE, projected_uv)
        * vertex_modulate;
}