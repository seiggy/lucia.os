// Procedural surfaces for the universe: planets, suns, the wormhole and the galaxy glow. Everything is generated from noise and colour,
// so no textures ship with the app.
const noise = /* glsl */ `
vec3 mod289(vec3 x){return x-floor(x*(1.0/289.0))*289.0;}
vec4 mod289(vec4 x){return x-floor(x*(1.0/289.0))*289.0;}
vec4 permute(vec4 x){return mod289(((x*34.0)+10.0)*x);}
vec4 taylorInvSqrt(vec4 r){return 1.79284291400159-0.85373472095314*r;}
float snoise(vec3 v){
  const vec2 C=vec2(1.0/6.0,1.0/3.0); const vec4 D=vec4(0.0,0.5,1.0,2.0);
  vec3 i=floor(v+dot(v,C.yyy)); vec3 x0=v-i+dot(i,C.xxx);
  vec3 g=step(x0.yzx,x0.xyz); vec3 l=1.0-g; vec3 i1=min(g.xyz,l.zxy); vec3 i2=max(g.xyz,l.zxy);
  vec3 x1=x0-i1+C.xxx; vec3 x2=x0-i2+C.yyy; vec3 x3=x0-D.yyy;
  i=mod289(i);
  vec4 p=permute(permute(permute(i.z+vec4(0.0,i1.z,i2.z,1.0))+i.y+vec4(0.0,i1.y,i2.y,1.0))+i.x+vec4(0.0,i1.x,i2.x,1.0));
  float n_=0.142857142857; vec3 ns=n_*D.wyz-D.xzx;
  vec4 j=p-49.0*floor(p*ns.z*ns.z); vec4 x_=floor(j*ns.z); vec4 y_=floor(j-7.0*x_);
  vec4 x=x_*ns.x+ns.yyyy; vec4 y=y_*ns.x+ns.yyyy; vec4 h=1.0-abs(x)-abs(y);
  vec4 b0=vec4(x.xy,y.xy); vec4 b1=vec4(x.zw,y.zw);
  vec4 s0=floor(b0)*2.0+1.0; vec4 s1=floor(b1)*2.0+1.0; vec4 sh=-step(h,vec4(0.0));
  vec4 a0=b0.xzyw+s0.xzyw*sh.xxyy; vec4 a1=b1.xzyw+s1.xzyw*sh.zzww;
  vec3 p0=vec3(a0.xy,h.x); vec3 p1=vec3(a0.zw,h.y); vec3 p2=vec3(a1.xy,h.z); vec3 p3=vec3(a1.zw,h.w);
  vec4 norm=taylorInvSqrt(vec4(dot(p0,p0),dot(p1,p1),dot(p2,p2),dot(p3,p3)));
  p0*=norm.x; p1*=norm.y; p2*=norm.z; p3*=norm.w;
  vec4 m=max(0.5-vec4(dot(x0,x0),dot(x1,x1),dot(x2,x2),dot(x3,x3)),0.0); m=m*m;
  return 105.0*dot(m*m,vec4(dot(p0,x0),dot(p1,x1),dot(p2,x2),dot(p3,x3)));
}
float fbm(vec3 p){float a=0.5,s=0.0;for(int i=0;i<5;i++){s+=a*snoise(p);p*=2.03;a*=0.5;}return s;}
`

// One instanced mesh draws every planet and moon. Each instance has a base colour (instanceColor), an atmosphere colour, the star that
// lights it, and a seed and kind: 0 rocky, 1 ocean, 2 gas giant, 3 ice.
export const planetVertex = /* glsl */ `
attribute vec3 aAtmos; attribute vec3 aStar; attribute vec2 aSeedKind;
varying vec3 vObj; varying vec3 vN; varying vec3 vView; varying vec3 vWorld; varying vec3 vBase; varying vec3 vAtmos; varying vec3 vStar; varying vec2 vSeedKind;
void main(){
  mat4 m = modelMatrix * instanceMatrix;
  vec4 w = m * vec4(position, 1.0);
  vObj = position; vWorld = w.xyz; vN = normalize(mat3(m) * normal); vView = cameraPosition - w.xyz;
  vBase = instanceColor; vAtmos = aAtmos; vStar = aStar; vSeedKind = aSeedKind;
  gl_Position = projectionMatrix * viewMatrix * w;
}`
export const planetFragment = /* glsl */ `
varying vec3 vObj; varying vec3 vN; varying vec3 vView; varying vec3 vWorld; varying vec3 vBase; varying vec3 vAtmos; varying vec3 vStar; varying vec2 vSeedKind;
${noise}
void main(){
  vec3 p = normalize(vObj); float seed = vSeedKind.x; float kind = vSeedKind.y; vec3 col;
  if (kind < 0.5) {
    float h = fbm(p * 3.2 + seed * 17.0); float c = abs(snoise(p * 9.0 + seed * 5.0));
    col = mix(vBase * 0.28, vBase * 1.1, smoothstep(-0.35, 0.65, h)); col *= 0.82 + 0.3 * smoothstep(0.1, 0.5, c);
  } else if (kind < 1.5) {
    float h = fbm(p * 2.3 + seed * 9.0); float land = smoothstep(-0.02, 0.06, h);
    vec3 water = mix(vec3(0.02, 0.08, 0.26), vec3(0.06, 0.32, 0.52), 0.5 + 0.5 * h);
    vec3 ground = mix(vBase * 0.45, vBase * 1.05, smoothstep(0.08, 0.5, h));
    col = mix(water, ground, land); col = mix(col, vec3(0.92, 0.96, 1.0), smoothstep(0.76, 0.92, abs(p.y) + 0.06 * h));
    float cloud = smoothstep(0.15, 0.6, fbm(p * 3.6 + seed * 3.0 + vec3(0.0, 4.0, 0.0))); col = mix(col, vec3(1.0), cloud * 0.35);
  } else if (kind < 2.5) {
    float warp = fbm(vec3(p.x * 1.6, p.y * 4.0, p.z * 1.6) + seed * 7.0) * 0.55;
    float band = sin(p.y * 10.0 + warp * 4.0 + seed * 6.0); float fine = sin(p.y * 31.0 + warp * 9.0) * 0.25;
    col = mix(vBase * 0.5, vBase * 1.3, 0.5 + 0.5 * (band + fine) * 0.8);
    col = mix(col, vBase * 1.5, smoothstep(0.55, 0.95, snoise(vec3(p.x * 2.0, p.y * 6.0, p.z * 2.0) + seed)) * 0.4);
  } else {
    float h = fbm(p * 4.0 + seed * 5.0); float crack = smoothstep(0.92, 1.0, 1.0 - abs(snoise(p * 7.0 + seed * 2.0)));
    col = mix(vec3(0.5, 0.7, 0.86) * 0.7 + vBase * 0.15, vec3(0.96, 0.99, 1.0), smoothstep(-0.3, 0.7, h)); col = mix(col, vec3(0.3, 0.5, 0.75), crack * 0.6);
  }
  vec3 n = normalize(vN); vec3 L = normalize(vStar - vWorld); vec3 V = normalize(vView);
  float ndl = max(dot(n, L), 0.0); float lit = 0.07 + ndl * 1.05;
  float fres = pow(1.0 - max(dot(n, V), 0.0), 2.6);
  float term = smoothstep(-0.15, 0.25, dot(n, L));
  vec3 color = col * lit + vAtmos * fres * (0.18 + 1.1 * term);
  gl_FragColor = vec4(color, 1.0);
}`

export const sunVertex = /* glsl */ `
varying vec3 vObj; varying vec3 vN; varying vec3 vView;
void main(){ vec4 w = modelMatrix * vec4(position, 1.0); vObj = position; vN = normalize(mat3(modelMatrix) * normal); vView = cameraPosition - w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }`
export const sunFragment = /* glsl */ `
uniform float uTime; uniform vec3 uColor; varying vec3 vObj; varying vec3 vN; varying vec3 vView;
${noise}
void main(){
  vec3 p = normalize(vObj);
  float f = fbm(p * 3.0 + vec3(uTime * 0.04, 0.0, uTime * 0.03)); float g = fbm(p * 8.0 - uTime * 0.06);
  float heat = 0.55 + 0.45 * f + 0.18 * g;
  float edge = pow(1.0 - max(dot(normalize(vN), normalize(vView)), 0.0), 2.0);
  vec3 col = mix(uColor * 0.7, vec3(1.0, 0.97, 0.9), clamp(heat - 0.3, 0.0, 1.0));
  col = col * (0.9 + 0.5 * heat) + uColor * edge * 0.8;
  gl_FragColor = vec4(col, 1.0);
}`

export const glowVertex = /* glsl */ `
varying vec2 vUv;
void main(){ vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`
// A soft round glow for a sun's corona.
export const coronaFragment = /* glsl */ `
uniform vec3 uColor; uniform float uPower; varying vec2 vUv;
void main(){ float r = length(vUv * 2.0 - 1.0); if (r > 1.0) discard; float a = pow(1.0 - r, uPower); gl_FragColor = vec4(uColor * a * 0.8, a); }`
// The wormhole's mouth, seen head on: a vortex of blue and amber arms winding into a white-hot centre, with a bright rim. uA and uB are the two
// arm colours.
export const portalFragment = /* glsl */ `
uniform float uTime; uniform vec3 uA; uniform vec3 uB; varying vec2 vUv;
${noise}
void main(){
  vec2 p = vUv * 2.0 - 1.0; float r = length(p); if (r > 1.0) discard;
  float a = atan(p.y, p.x);
  float warp = snoise(vec3(p * 2.6, uTime * 0.35));
  float arms = pow(0.5 + 0.5 * sin(a * 3.0 - log(r + 0.03) * 7.5 + uTime * 2.4 + 1.6 * warp), 3.0);
  float fine = pow(0.5 + 0.5 * sin(a * 7.0 + log(r + 0.03) * 13.0 - uTime * 3.1 + 2.2 * warp), 6.0);
  float core = exp(-r * 3.4);
  vec3 col = mix(uA, uB, arms) * (0.2 + arms * 0.9 + fine * 0.6) * smoothstep(0.0, 0.25, r) + vec3(1.0, 0.96, 0.9) * core * 0.6;
  float rim = smoothstep(0.78, 0.95, r) * (1.0 - smoothstep(0.97, 1.0, r));
  col += mix(uA, vec3(1.0), 0.4) * rim * 1.1;
  float alpha = (1.0 - smoothstep(0.95, 1.0, r)) * (0.6 + 0.4 * core + 0.3 * arms);
  gl_FragColor = vec4(col, alpha);
}`
// A jumpgate's field: a translucent sheet of deep blue energy with soft light drifting slowly across it, edged by a white hairline.
export const gateFragment = /* glsl */ `
uniform float uTime; uniform vec3 uA; varying vec2 vUv;
${noise}
void main(){
  vec2 p = vUv * 2.0 - 1.0; float r = length(p); if (r > 1.0) discard;
  float drift = 0.5 + 0.5 * snoise(vec3(p.x * 0.9 - uTime * 0.12, p.y * 2.4, uTime * 0.08));
  float sheen = 0.5 + 0.5 * snoise(vec3(p * 1.3, uTime * 0.15));
  vec3 deep = vec3(0.0, 0.04, 0.3), bright = mix(vec3(0.05, 0.35, 0.9), uA, 0.15);
  vec3 col = mix(deep, bright, 0.15 + 0.25 * pow(r, 4.0) + 0.3 * drift * sheen) * 0.6;
  float alpha = (1.0 - smoothstep(0.97, 1.0, r)) * (0.26 + 0.24 * pow(r, 4.0));
  // About a pixel wide at any distance.
  float line = exp(-pow((r - 0.975) / max(fwidth(r) * 0.8, 1e-4), 2.0));
  gl_FragColor = vec4(col * alpha + vec3(0.85, 0.95, 1.0) * line * 0.8, 1.0);
}`
// The tunnel behind the mouth: a trumpet that narrows to a throat, crossed by swirling filaments of light and white-hot at its far end.
// u runs around it and v along it, 0 at the mouth to 1 at the throat.
export const funnelFragment = /* glsl */ `
uniform float uTime; uniform vec3 uA; uniform vec3 uB; varying vec2 vUv;
${noise}
void main(){
  float a = vUv.x * 6.2831853, v = vUv.y;
  float flow = snoise(vec3(cos(a) * 1.6, sin(a) * 1.6, v * 4.0 - uTime * 0.8));
  float f1 = 0.5 + 0.5 * sin(a * 3.0 + v * 11.0 - uTime * 1.7 + 1.6 * flow);
  float f2 = 0.5 + 0.5 * sin(a * 5.0 - v * 8.0 + uTime * 1.2 + 2.1 * snoise(vec3(cos(a) * 2.2, sin(a) * 2.2, v * 6.0 + uTime * 0.5)));
  float filament = pow(f1, 6.0) * 0.5 + pow(f2, 9.0) * 0.6;
  float throat = smoothstep(0.5, 1.0, v);
  vec3 col = mix(uA, uB, f2) * (0.16 + filament * 1.0) + vec3(1.0, 0.97, 0.92) * throat * throat * 0.7;
  float fade = smoothstep(0.0, 0.08, v);
  gl_FragColor = vec4(col * fade, (0.22 + filament * 0.4 + throat * 0.5) * fade);
}`
export const ringVertex = /* glsl */ `
varying float vR; varying vec2 vXY;
void main(){ vXY = position.xy; vR = length(position.xy); gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`
export const ringFragment = /* glsl */ `
uniform vec3 uColor; uniform float uInner; uniform float uOuter; varying float vR; varying vec2 vXY;
void main(){
  float t = (vR - uInner) / (uOuter - uInner);
  float bands = 0.55 + 0.45 * sin(t * 70.0) * sin(t * 23.0 + 1.0);
  float gap = smoothstep(0.52, 0.55, t) * (1.0 - smoothstep(0.58, 0.61, t));
  float alpha = smoothstep(0.0, 0.08, t) * (1.0 - smoothstep(0.88, 1.0, t)) * bands * (1.0 - 0.85 * gap) * 0.7;
  gl_FragColor = vec4(uColor * (0.35 + 0.35 * bands), alpha);
}`
// A galaxy's glow on the plane: a bright core and spiral arms fading out.
export const galaxyFragment = /* glsl */ `
uniform vec3 uColor; uniform vec3 uColor2; uniform float uArms; uniform float uHole; uniform float uFade; uniform float uWind; uniform float uPhase; varying vec2 vUv;
${noise}
void main(){
  vec2 p = vUv * 2.0 - 1.0; float r = length(p); if (r > 1.0) discard;
  // The plane lies flat with its v axis along -z, so the angle runs the other way from the star arms.
  float a = -atan(p.y, p.x), warp = fbm(vec3(p * 3.5, 2.0));
  float phase = uArms * a - uWind * r - uPhase + warp * 1.4;
  float arm = pow(0.5 + 0.5 * cos(phase), 3.0), lane = pow(0.5 + 0.5 * cos(phase + 0.7), 12.0);
  float clump = smoothstep(-0.25, 0.6, fbm(vec3(p * 11.0, 7.0)));
  float disc = exp(-r * 5.0), bulge = exp(-r * r * 140.0);
  vec3 col = uColor * disc * 0.55 + uColor2 * arm * disc * (0.35 + clump) * 1.6 + uColor * bulge * 2.2;
  col *= 1.0 - lane * 0.6 * smoothstep(0.04, 0.15, r);
  col *= 1.0 - smoothstep(0.55, 1.0, r);
  col *= smoothstep(uHole * 0.7, uHole * 1.3, r);
  gl_FragColor = vec4(col * 0.3 * uFade, 1.0);
}`

// A Wi-Fi link: a thin glowing line with short bright pulses running along it. Per vertex: how far along the link it is, and per pulse train its
// speed (negative runs the other way), how many pulses the link holds, and a seed that staggers them.
export const linkVertex = /* glsl */ `
attribute float aAlong; attribute vec3 aInfo;
varying float vAlong; varying vec3 vInfo;
void main(){ vAlong = aAlong; vInfo = aInfo; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`
export const linkFragment = /* glsl */ `
uniform float uTime; uniform vec3 uColor; varying float vAlong; varying vec3 vInfo;
void main(){
  float p = fract(vAlong * vInfo.y - uTime * vInfo.x + vInfo.z);
  float q = vInfo.x >= 0.0 ? p : 1.0 - p;
  float pulse = pow(q, 36.0);
  float ends = smoothstep(0.0, 0.1, vAlong) * smoothstep(0.0, 0.1, 1.0 - vAlong);
  float glow = (0.3 + 6.0 * pulse) * ends;
  gl_FragColor = vec4(uColor * glow, glow);
}`
// A solar system's faint dust on the ecliptic: smooth and centred on the sun, with no arms.
export const dustFragment = /* glsl */ `
uniform vec3 uColor; varying vec2 vUv;
void main(){ float r = length(vUv * 2.0 - 1.0); if (r > 1.0) discard; float a = pow(1.0 - r, 2.4) * 0.5 + exp(-r * 9.0) * 0.35; gl_FragColor = vec4(uColor * a * 0.32, 1.0); }`
// Belt dust: specks on circular orbits, advanced on the GPU so thousands cost nothing per frame. aOrbit is radius, start angle, height and angular speed.
export const dustVertex = /* glsl */ `
uniform float uTime; uniform vec3 uCenter; attribute vec4 aOrbit; attribute float aSize; varying float vShade;
void main(){
  float angle = aOrbit.y + aOrbit.w * uTime;
  vec4 view = modelViewMatrix * vec4(uCenter + vec3(cos(angle) * aOrbit.x, aOrbit.z, sin(angle) * aOrbit.x), 1.0);
  gl_Position = projectionMatrix * view;
  gl_PointSize = clamp(aSize * 260.0 / max(-view.z, 1.0), 1.6, 5.0);
  vShade = 0.55 + 0.45 * fract(aOrbit.y * 7.31 + aSize);
}`
export const speckFragment = /* glsl */ `
uniform vec3 uColor; varying float vShade;
void main(){ float d = length(gl_PointCoord - 0.5); if (d > 0.5) discard; float a = (1.0 - d * 2.0) * 0.75 * vShade; gl_FragColor = vec4(uColor * a, a); }`
// The black hole's accretion disc: hot gas in bands that swirl faster the closer they are, white-hot at the inner edge and cooling to red.
export const accretionFragment = /* glsl */ `
uniform float uTime; uniform float uInner; uniform float uOuter; varying float vR; varying vec2 vXY;
${noise}
void main(){
  float t = (vR - uInner) / (uOuter - uInner); float a = atan(vXY.y, vXY.x);
  float spin = uTime * 0.3 / (0.22 + t);
  float n = fbm(vec3(cos(a + spin) * (1.5 + t * 2.0), sin(a + spin) * (1.5 + t * 2.0), t * 3.0));
  float bands = 0.5 + 0.5 * sin(t * 46.0 + n * 5.0);
  vec3 hot = mix(vec3(1.0, 0.95, 0.85), vec3(1.0, 0.45, 0.12), smoothstep(0.0, 0.6, t));
  hot = mix(hot, vec3(0.5, 0.08, 0.05), smoothstep(0.55, 1.0, t));
  float heat = (1.0 - t) * (0.5 + 0.9 * n + 0.4 * bands);
  float alpha = smoothstep(0.0, 0.05, t) * (1.0 - smoothstep(0.7, 1.0, t));
  gl_FragColor = vec4(hot * heat * 0.95, alpha);
}`
// A gas cloud on the plane: soft, lumpy and two-toned, fading out at its edge.
export const nebulaFragment = /* glsl */ `
uniform vec3 uColor; uniform float uFade; varying vec2 vUv;
${noise}
void main(){
  if (uFade <= 0.0) discard;
  vec2 p = vUv * 2.0 - 1.0; float r = length(p); if (r > 1.0) discard;
  float n = fbm(vec3(p * 2.4, 1.7)), m = fbm(vec3(p * 4.1 + 9.0, 3.1));
  float body = smoothstep(-0.15, 0.55, n) * pow(1.0 - r, 1.3);
  vec3 col = mix(uColor, uColor.bgr * 1.1, smoothstep(-0.3, 0.5, m));
  gl_FragColor = vec4(col * body * 0.34 * uFade, 1.0);
}`
// Galaxy stars dim with distance, so from inside the disc only near stars are bright and the far ones pile up into a band.
export const starVertex = /* glsl */ `
attribute float scale; uniform float uRef; uniform float uSize; varying vec3 vColor;
void main(){
  vec4 mv = modelViewMatrix * vec4(position, 1.0);
  vColor = color * pow(clamp(uRef / max(-mv.z, 1.0), 0.0, 1.0), 0.7);
  gl_PointSize = uSize * scale;
  gl_Position = projectionMatrix * mv;
}`

// Dust and gas along the arms: big soft sprites sized in world units, each a lumpy blob turned its own way.
export const cloudVertex = /* glsl */ `
attribute vec3 aCloud; uniform float uScreen; varying vec3 vColor; varying vec2 vSpin; varying float vSeed;
void main(){
  vec4 mv = modelViewMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * mv;
  float px = aCloud.x * projectionMatrix[1][1] * uScreen / max(-mv.z, 1.0);
  gl_PointSize = min(px, 384.0);
  vColor = color * smoothstep(0.0, 2.0, px) * (1.0 - smoothstep(200.0, 384.0, px));
  vSpin = vec2(cos(aCloud.y), sin(aCloud.y)); vSeed = aCloud.z;
}`
export const cloudFragment = /* glsl */ `
uniform float uFade; varying vec3 vColor; varying vec2 vSpin; varying float vSeed;
${noise}
void main(){
  vec2 c = gl_PointCoord - 0.5; float d = length(c) * 2.0; if (d > 1.0) discard;
  c = mat2(vSpin.x, -vSpin.y, vSpin.y, vSpin.x) * c;
  float n = snoise(vec3(c * 3.2, vSeed)) * 0.6 + snoise(vec3(c * 7.0, vSeed + 5.0)) * 0.3;
  float a = pow(1.0 - d, 1.6) * smoothstep(-0.4, 0.7, n);
  gl_FragColor = vec4(vColor * a * uFade, 1.0);
}`

export const starFragment = /* glsl */ `
varying vec3 vColor;
void main(){
  vec2 c = gl_PointCoord - 0.5; if (dot(c, c) > 0.25) discard;
  gl_FragColor = vec4(vColor, 1.0);
}`
