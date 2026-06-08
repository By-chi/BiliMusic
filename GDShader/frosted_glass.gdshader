shader_type canvas_item;

uniform vec4 tint_color : source_color = vec4(0.9, 0.9, 1.0, 0.2);


uniform float blur_sigma : hint_range(0.5, 20.0) = 8.0;

uniform float blur_scale : hint_range(0.0, 2.0) = 0.5;

float gaussian(float x, float s) {
    return exp(-(x * x) / (2.0 * s * s)) / (sqrt(2.0 * 3.14159265359) * s);
}

void fragment() {
    vec2 ps = TEXTURE_PIXEL_SIZE;

    float sigma = blur_sigma * (1.0 + blur_scale * UV.y);

    vec3 blurred = vec3(0.0);
    float total_weight = 0.0;

    for (int x = -7; x <= 7; x++) {
        for (int y = -7; y <= 7; y++) {
            float dist = length(vec2(float(x), float(y)));
            float w = gaussian(dist, sigma);
            blurred += texture(TEXTURE, UV + vec2(float(x), float(y)) * ps).rgb * w;
            total_weight += w;
        }
    }

    blurred /= total_weight;

    vec3 final_color = mix(blurred, tint_color.rgb, tint_color.a);

    
    COLOR = vec4(final_color, 1.0);
}