shader_type canvas_item;
render_mode blend_add, skip_vertex_transform;
// NCS 粒子球 —— 频谱瀑布图驱动 + 点阵匀速滑移 + 流体多色渐变 + 振幅缩放
// 斐波那契球面点阵, 严格均匀
// 频谱纹理 = bands x history 滚动瀑布图:
//   方位角 -> 频段, |纬度| -> 时间 (赤道=此刻, 波形向两极流淌)
// 点阵绕 y 轴匀速滑移 (像地球仪自转, 点流过球面)
// 流体式多主题色渐变: 噪声域漂移, 色块如油墨在球面溶动, 无旋转轴
// 噪声扰动持续蠕动 (流速随音量), 静音时近光滑球壳
// 整体实时振幅缩放: 音量驱动整球呼吸胀缩
// 3D 感 = 朗伯明暗 + 粒子近大远小
uniform vec2 screen_size = vec2(1920.0, 1080.0);
uniform vec2 screen_center = vec2(960.0, 540.0);

uniform float sphere_radius = 275.0;
uniform float spectrum_mult = 230.0;      // 频谱径向推起幅度 (px)
uniform float particle_count = 7400.0;
uniform float amp_scale = 1.0;            // 整体振幅缩放 (1.0 = 原始大小), 脚本按音量驱动

uniform sampler2D spectrum_tex : repeat_enable, filter_linear;
uniform float band_count = 90.0;

uniform float spin_angle = 0.0;           // 绕 y 轴累计角度 (弧度), 脚本匀速递增

uniform float final_audio = 0.0;          // 全频段平均能量
uniform float f_scale = 4.6;
uniform float octave_multiplier = 0.25;
uniform float complexity = 3.0;
uniform float displace_amt = 70.0;
uniform vec4 flow = vec4(0.0, 0.033, 0.0, 0.015);

uniform vec3 light_dir = vec3(0.25, 0.45, 1.0);
uniform float ambient = 0.25;

uniform float dot_radius = 2.5;
uniform float opacity = 0.6;
uniform float enter = 1.0;

uniform vec3 palette0 = vec3(0.0118, 0.1412, 0.3412);
uniform vec3 palette1 = vec3(0.2, 0.5, 0.9);
uniform vec3 palette2 = vec3(0.6, 0.2, 0.8);
uniform vec3 palette3 = vec3(0.9, 0.4, 0.3);
uniform float palette_count = 1.0;        // 实际使用的颜色数 (1~4)
uniform float color_lat_mix = 0.25;       // 纬度参与颜色场的比重

varying float v_b;
varying float v_scale;
varying vec3 v_col;

vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x * 34.0) + 1.0) * x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }
vec4 fade(vec4 t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }

float cnoise(vec4 P) {
    vec4 Pi0 = floor(P);
    vec4 Pi1 = Pi0 + 1.0;
    vec4 Pf0 = fract(P);
    vec4 Pf1 = Pf0 - 1.0;
    vec4 ix = vec4(Pi0.x, Pi1.x, Pi0.x, Pi1.x);
    vec4 iy = vec4(Pi0.yy, Pi1.yy);
    vec4 iz0 = vec4(Pi0.zzzz);
    vec4 iz1 = vec4(Pi1.zzzz);
    vec4 iw0 = vec4(Pi0.wwww);
    vec4 iw1 = vec4(Pi1.wwww);
    vec4 ixy = permute(permute(ix) + iy);
    vec4 ixy0 = permute(ixy + iz0);
    vec4 ixy1 = permute(ixy + iz1);
    vec4 ixy00 = permute(ixy0 + iw0);
    vec4 ixy01 = permute(ixy0 + iw1);
    vec4 ixy10 = permute(ixy1 + iw0);
    vec4 ixy11 = permute(ixy1 + iw1);
    vec4 gx00 = ixy00 / 7.0;
    vec4 gy00 = floor(gx00) / 7.0;
    vec4 gz00 = floor(gy00) / 6.0;
    gx00 = fract(gx00) - 0.5;
    gy00 = fract(gy00) - 0.5;
    gz00 = fract(gz00) - 0.5;
    vec4 gw00 = vec4(0.75) - abs(gx00) - abs(gy00) - abs(gz00);
    vec4 sw00 = step(gw00, vec4(0.0));
    gx00 -= sw00 * (step(vec4(0.0), gx00) - 0.5);
    gy00 -= sw00 * (step(vec4(0.0), gy00) - 0.5);
    vec4 gx01 = ixy01 / 7.0;
    vec4 gy01 = floor(gx01) / 7.0;
    vec4 gz01 = floor(gy01) / 6.0;
    gx01 = fract(gx01) - 0.5;
    gy01 = fract(gy01) - 0.5;
    gz01 = fract(gz01) - 0.5;
    vec4 gw01 = vec4(0.75) - abs(gx01) - abs(gy01) - abs(gz01);
    vec4 sw01 = step(gw01, vec4(0.0));
    gx01 -= sw01 * (step(vec4(0.0), gx01) - 0.5);
    gy01 -= sw01 * (step(vec4(0.0), gy01) - 0.5);
    vec4 gx10 = ixy10 / 7.0;
    vec4 gy10 = floor(gx10) / 7.0;
    vec4 gz10 = floor(gy10) / 6.0;
    gx10 = fract(gx10) - 0.5;
    gy10 = fract(gy10) - 0.5;
    gz10 = fract(gz10) - 0.5;
    vec4 gw10 = vec4(0.75) - abs(gx10) - abs(gy10) - abs(gz10);
    vec4 sw10 = step(gw10, vec4(0.0));
    gx10 -= sw10 * (step(vec4(0.0), gx10) - 0.5);
    gy10 -= sw10 * (step(vec4(0.0), gy10) - 0.5);
    vec4 gx11 = ixy11 / 7.0;
    vec4 gy11 = floor(gx11) / 7.0;
    vec4 gz11 = floor(gy11) / 6.0;
    gx11 = fract(gx11) - 0.5;
    gy11 = fract(gy11) - 0.5;
    gz11 = fract(gz11) - 0.5;
    vec4 gw11 = vec4(0.75) - abs(gx11) - abs(gy11) - abs(gz11);
    vec4 sw11 = step(gw11, vec4(0.0));
    gx11 -= sw11 * (step(vec4(0.0), gx11) - 0.5);
    gy11 -= sw11 * (step(vec4(0.0), gy11) - 0.5);
    vec4 g0000 = vec4(gx00.x, gy00.x, gz00.x, gw00.x);
    vec4 g1000 = vec4(gx00.y, gy00.y, gz00.y, gw00.y);
    vec4 g0100 = vec4(gx00.z, gy00.z, gz00.z, gw00.z);
    vec4 g1100 = vec4(gx00.w, gy00.w, gz00.w, gw00.w);
    vec4 g0010 = vec4(gx10.x, gy10.x, gz10.x, gw10.x);
    vec4 g1010 = vec4(gx10.y, gy10.y, gz10.y, gw10.y);
    vec4 g0110 = vec4(gx10.z, gy10.z, gz10.z, gw10.z);
    vec4 g1110 = vec4(gx10.w, gy10.w, gz10.w, gw10.w);
    vec4 g0001 = vec4(gx01.x, gy01.x, gz01.x, gw01.x);
    vec4 g1001 = vec4(gx01.y, gy01.y, gz01.y, gw01.y);
    vec4 g0101 = vec4(gx01.z, gy01.z, gz01.z, gw01.z);
    vec4 g1101 = vec4(gx01.w, gy01.w, gz01.w, gw01.w);
    vec4 g0011 = vec4(gx11.x, gy11.x, gz11.x, gw11.x);
    vec4 g1011 = vec4(gx11.y, gy11.y, gz11.y, gw11.y);
    vec4 g0111 = vec4(gx11.z, gy11.z, gz11.z, gw11.z);
    vec4 g1111 = vec4(gx11.w, gy11.w, gz11.w, gw11.w);
    vec4 norm00 = taylorInvSqrt(vec4(dot(g0000, g0000), dot(g0100, g0100), dot(g1000, g1000), dot(g1100, g1100)));
    g0000 *= norm00.x; g0100 *= norm00.y; g1000 *= norm00.z; g1100 *= norm00.w;
    vec4 norm01 = taylorInvSqrt(vec4(dot(g0001, g0001), dot(g0101, g0101), dot(g1001, g1001), dot(g1101, g1101)));
    g0001 *= norm01.x; g0101 *= norm01.y; g1001 *= norm01.z; g1101 *= norm01.w;
    vec4 norm10 = taylorInvSqrt(vec4(dot(g0010, g0010), dot(g0110, g0110), dot(g1010, g1010), dot(g1110, g1110)));
    g0010 *= norm10.x; g0110 *= norm10.y; g1010 *= norm10.z; g1110 *= norm10.w;
    vec4 norm11 = taylorInvSqrt(vec4(dot(g0011, g0011), dot(g0111, g0111), dot(g1011, g1011), dot(g1111, g1111)));
    g0011 *= norm11.x; g0111 *= norm11.y; g1011 *= norm11.z; g1111 *= norm11.w;
    float n0000 = dot(g0000, Pf0);
    float n1000 = dot(g1000, vec4(Pf1.x, Pf0.yzw));
    float n0100 = dot(g0100, vec4(Pf0.x, Pf1.y, Pf0.zw));
    float n1100 = dot(g1100, vec4(Pf1.xy, Pf0.zw));
    float n0010 = dot(g0010, vec4(Pf0.xy, Pf1.z, Pf0.w));
    float n1010 = dot(g1010, vec4(Pf1.x, Pf0.y, Pf1.z, Pf0.w));
    float n0110 = dot(g0110, vec4(Pf0.x, Pf1.yz, Pf0.w));
    float n1110 = dot(g1110, vec4(Pf1.xyz, Pf0.w));
    float n0001 = dot(g0001, vec4(Pf0.xyz, Pf1.w));
    float n1001 = dot(g1001, vec4(Pf1.x, Pf0.yz, Pf1.w));
    float n0101 = dot(g0101, vec4(Pf0.x, Pf1.y, Pf0.z, Pf1.w));
    float n1101 = dot(g1101, vec4(Pf1.xy, Pf0.z, Pf1.w));
    float n0011 = dot(g0011, vec4(Pf0.xy, Pf1.zw));
    float n1011 = dot(g1011, vec4(Pf1.x, Pf0.y, Pf1.zw));
    float n0111 = dot(g0111, vec4(Pf0.x, Pf1.yzw));
    float n1111 = dot(g1111, Pf1);
    vec4 fade_xyzw = fade(Pf0);
    vec4 n_0w = mix(vec4(n0000, n1000, n0100, n1100), vec4(n0001, n1001, n0101, n1101), fade_xyzw.w);
    vec4 n_1w = mix(vec4(n0010, n1010, n0110, n1110), vec4(n0011, n1011, n0111, n1111), fade_xyzw.w);
    vec4 n_zw = mix(n_0w, n_1w, fade_xyzw.z);
    vec2 n_yzw = mix(n_zw.xy, n_zw.zw, fade_xyzw.y);
    float n_xyzw = mix(n_yzw.x, n_yzw.y, fade_xyzw.x);
    return 2.2 * n_xyzw;
}
float fbm_dir(vec3 dir) {
    float total = 0.0;
    float amp = 1.0;
    float freq = 1.0;
    float value = 0.0;
    for (int i = 0; i < int(complexity); i += 1) {
        vec4 p = vec4(dir * f_scale * freq, 0.0) + flow * TIME;
        value += cnoise(p) * amp;
        total += amp;
        amp *= octave_multiplier;
        freq *= 1.0;
    }
    return value / total;
}
vec3 palette_sample(float t) {
    float n = max(palette_count, 1.0);
    if (n <= 1.5) return palette0;
    float x = t * n;
    float i = floor(x);
    float f = smoothstep(0.15, 0.85, fract(x));
    vec3 a = palette0;
    vec3 b = palette0;
    if (i < 0.5)      { a = palette0; b = palette1; }
    else if (i < 1.5) { a = palette1; b = palette2; }
    else if (i < 2.5) { a = palette2; b = palette3; }
    else              { a = palette3; b = palette0; }
    return mix(a, b, f);
}

void vertex() {
    float n = particle_count;
    float fi = float(INSTANCE_ID);
    float y = 1.0 - 2.0 * (fi + 0.5) / n;
    float rr = sqrt(max(1.0 - y * y, 0.0));
    float ang = fi * 2.39996323;
    vec3 dir = vec3(cos(ang) * rr, y, sin(ang) * rr);
    float sa = sin(spin_angle);
    float ca = cos(spin_angle);
    dir = vec3(ca * dir.x + sa * dir.z, dir.y, -sa * dir.x + ca * dir.z);
    float az = atan(dir.y, dir.x);
    float u = fract(az / 6.2831853);
    float v = clamp(abs(dir.y), 0.0, 1.0);
    float spec = texture(spectrum_tex, vec2(u, v)).r;
    float on = final_audio * fbm_dir(dir + vec3(TIME * 0.12, TIME * 0.07, TIME * 0.09));
    on = clamp(on, -5.0, 5.0);
    float Rn = (sphere_radius + spectrum_mult * spec
             + displace_amt * (0.15 + final_audio) * on) * amp_scale;

    vec3 p = vec3(screen_center, 0.0) + dir * Rn;
    vec3 L = normalize(light_dir);
    float diff = max(dot(dir, L), 0.0);
    v_scale = 0.72 + 0.38 * max(dir.z, 0.0);
    v_b = (ambient + (1.0 - ambient) * diff) * (0.5 + 1.6 * spec + 0.3 * final_audio);

    // ---- 流体式渐变: 噪声域随时间漂移, 色块在球面溶动, 无旋转轴 ----
    vec3 cp = dir * 1.6 + vec3(TIME * 0.05, TIME * 0.035, -TIME * 0.04);
    float warp = cnoise(vec4(cp * 0.7, TIME * 0.08));
    float ct = cnoise(vec4(cp + warp * 0.7, 0.0));
    v_col = palette_sample(ct * 0.5 + 0.5 + dir.y * color_lat_mix);

    VERTEX = p.xy + VERTEX * v_scale;
}
void fragment() {
    vec2 ij = (UV - 0.5) * (2.0 * dot_radius + 1.0);
    float d = length(ij);
    float w = 1.0 - smoothstep(dot_radius * v_scale - 0.75, dot_radius * v_scale + 0.25, d);
    float a = w * v_b * opacity * enter;
    COLOR = vec4(v_col, a);
}