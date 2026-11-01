/*
 * SerialScope FireWater / JustFloat demo firmware
 *
 * 默认使用 FireWater（ASCII 逗号分隔 + 换行），每 5 ms 发送一次。
 * 将 USE_JUSTFLOAT 改为 true 可以通过 JustFloat 二进制协议发送。
 *
 * 接线：开发板 USB 串口 -> SerialScope 中选择对应 COM 口和 115200 波特率。
 */

const bool USE_JUSTFLOAT = false;
const unsigned long PERIOD_US = 5000;  // 200 Hz

unsigned long lastMicros = 0;
float phase = 0.0f;

void setup() {
  Serial.begin(115200);
  while (!Serial && millis() < 2000) {
    // 某些 USB CDC 开发板需要等待串口枚举
  }
}

void loop() {
  unsigned long now = micros();
  if (now - lastMicros < PERIOD_US) {
    return;
  }
  lastMicros = now;
  phase += 0.002f;

  float ch1 = 55.0f + 28.0f * sin(2.0f * PI * 0.45f * phase * 100.0f);
  float ch2 = 24.0f * cos(2.0f * PI * 0.23f * phase * 100.0f + 0.8f);
  float ch3 = -18.0f + 16.0f * sin(2.0f * PI * 0.70f * phase * 100.0f + 1.4f);

  if (USE_JUSTFLOAT) {
    float frame[3] = { ch1, ch2, ch3 };
    Serial.write(reinterpret_cast<const uint8_t*>(frame), sizeof(frame));
    const uint8_t tail[4] = { 0x00, 0x00, 0x80, 0x7F };  // +Infinity
    Serial.write(tail, sizeof(tail));
  } else {
    Serial.print(ch1, 4);
    Serial.print(',');
    Serial.print(ch2, 4);
    Serial.print(',');
    Serial.println(ch3, 4);
  }
}
