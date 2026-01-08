shader_type spatial;
render_mode cull_back, diffuse_lambert, specular_disabled;

uniform sampler2D atlas_tex : source_color;
uniform int grid_width = 16;
uniform int grid_height = 16;
uniform float block_size : hint_range(0.1, 64.0) = 1.0;

varying vec2 v_tile_uv0;
varying vec3 v_world_pos;
varying vec3 v_world_normal;

void vertex() {
    v_tile_uv0 = UV;
    v_world_pos    = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);
}

vec2 get_world_uv(vec3 wp, vec3 n) {
    vec2 coord;
    n = normalize(n);
    bool is_side = false;

    if (abs(n.y) > 0.5) {
        // top / bottom
        coord = wp.xz / block_size;
    } else if (abs(n.x) > 0.5) {
        // left / right
        coord = wp.zy / block_size;
        is_side = true;
    } else {
        // front / back
        coord = wp.xy / block_size;
        is_side = true;
    }

    vec2 cell_size = vec2(1.0 / float(grid_width), 1.0 / float(grid_height));

    vec2 local = fract(coord); // 0..1 in tile
    if (is_side) {
        // flip verticaal zodat gras naar boven wijst
        local.y = 1.0 - local.y;
    }

    local *= cell_size;
    return v_tile_uv0 + local;
}

vec2 snap_to_texel(vec2 uv) {
    vec2 tex_size = vec2(textureSize(atlas_tex, 0));
    return (floor(uv * tex_size) + 0.5) / tex_size;
}

void fragment() {
    vec2 uv = get_world_uv(v_world_pos, v_world_normal);
    uv = snap_to_texel(uv);
    ALBEDO = textureLod(atlas_tex, uv, 0.0).rgb;
}
