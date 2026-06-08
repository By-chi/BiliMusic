shader_type canvas_item;


uniform sampler2D blur_texture : source_color, filter_linear_mipmap;
uniform float flow_speed : hint_range(0.0, 5.0) = 1.0;
uniform float flow_strength : hint_range(0.0, 1.0) = 1.0;
uniform float flow_scale : hint_range(1.0, 40.0) = 2.0;
uniform float darken_gradient : hint_range(0.0, 1.0) = 0.1;


uniform float chromatic_aberration : hint_range(0.0, 0.02) = 0.006; 
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.35;       // 渐晕暗角强度
uniform vec3 tint_color : source_color = vec3(1.0);                  // 色调（默认无影响）
uniform float brightness : hint_range(0.5, 2.0) = 1.0;               // 亮度
uniform float edge_glow_strength : hint_range(0.0, 1.0) = 0.15;      // 边缘流光强度
uniform float glow_speed : hint_range(0.0, 2.0) = 0.4;               // 流光速度

void fragment() {
    vec2 tex_pixel_size = 1.0 / vec2(textureSize(blur_texture, 0));
    float time = TIME * flow_speed;

    
    vec2 distortion = vec2(
        sin(UV.y * flow_scale + time) * cos(UV.x * flow_scale * 0.7 + time * 1.3),
        cos(UV.x * flow_scale + time * 1.1) * sin(UV.y * flow_scale * 0.6 + time * 0.9)
    );
    float detail_scale = flow_scale * 2.5;
    distortion += vec2(
        sin(UV.y * detail_scale + time * 1.7) * cos(UV.x * detail_scale * 0.6 + time * 0.5),
        cos(UV.x * detail_scale + time * 0.8) * sin(UV.y * detail_scale * 0.7 + time * 1.1)
    ) * 0.5;

    float vertical_fade = smoothstep(0.0, 0.7, UV.y);
    distortion *= (1.0 + vertical_fade * 1.2);

    vec2 distorted_uv = UV + distortion * flow_strength * tex_pixel_size * 100.0;
    distorted_uv = clamp(distorted_uv, 0.0, 1.0);

    
    vec2 to_center = distorted_uv - 0.5;
    vec2 ca_offset = to_center * chromatic_aberration; // 边缘更强，中心为0
    float r = texture(blur_texture, distorted_uv + ca_offset).r;
    float g = texture(blur_texture, distorted_uv).g;
    float b = texture(blur_texture, distorted_uv - ca_offset).b;
    vec3 color = vec3(r, g, b);

    
    float gradient = 1.0 - darken_gradient * UV.y;
    color *= gradient;

    // --- 色调与亮度 ---
    color *= tint_color * brightness;

    
    float dist = length(UV - 0.5) * 1.6; // 范围缩放
    float vignette = 1.0 - dist * vignette_strength;
    vignette = clamp(vignette, 0.0, 1.0);
    
    vignette = smoothstep(0.0, 1.0, vignette); // 本身值已平滑，此处保证无硬边
    color *= vignette;

    
    float angle = atan(UV.y - 0.5, UV.x - 0.5); // -PI 到 PI
    
    float wave1 = sin(angle * 3.0 + time * glow_speed) * 0.5 + 0.5;
    float wave2 = sin(angle * 5.0 - time * glow_speed * 0.7) * 0.3 + 0.5;
    float glow_wave = (wave1 * 0.6 + wave2 * 0.4); // 混合后取值0~1
    
    float edge_mask = smoothstep(0.6, 0.9, dist); // 边缘区域
    vec3 glow_color = vec3(0.3, 0.5, 1.0) * glow_wave * edge_glow_strength * edge_mask;
    color += glow_color;

    
    COLOR = vec4(color, 1.0);
}