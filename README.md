# miku-on-my-window · Miku Desktop

설치 없이 실행하는 Windows 10/11 x64 미쿠 데스크톱 펫입니다.

`MikuDesktop.exe`를 실행하고, 알림 영역의 미쿠 아이콘에서 설정하거나 종료하세요.
자세한 조작은 `사용법.txt`에 있습니다.

## 다른 PC에서 개발하기

이 저장소를 복제(Clone)하고 해당 폴더를 Codex의 로컬 프로젝트로 여세요.
새 Codex 채팅에서는 `AGENTS.md`, `HANDOFF.md`와 소스를 읽고 이어가도록 요청하세요.
현재 구현 상태와 아직 구현하지 않은 기능은 `HANDOFF.md`에 정리했습니다.

Windows 10/11 x64에서 PowerShell로 빌드합니다. 별도 Node/Python/브라우저 런타임은 필요 없습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

저장소 루트에 생성된 `MikuDesktop.exe`를 실행하세요. 실행 중인 앱을 교체하려면 먼저 트레이에서 정상 종료하세요.
검증은 앱을 종료한 뒤 다음 명령으로 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\verify.ps1
```

실행 파일, ZIP, PC별 위치/배율 설정은 Git에 포함하지 않습니다. 소스와 스프라이트는 포함되어 있어 새 PC에서 다시 빌드할 수 있습니다.

## 동작

- 타이핑: 8번 생각·집중 동작만 사용
- 마지막 입력 후 1.5초 유지한 뒤 애니메이션 종료를 기다리지 않고 전환
- 대기 첫 번째 원본 프레임 제외, 나머지 5프레임 사용
- 동작 사이에 보간 프레임 없음
- 대기 깜빡임: 약 5.3초에 한 번
- 커서 시선: 16방향, 마지막 이동 후 1초 유지
- 미쿠 위에 커서: 반복 점프
- 드래그: 좌우 달리기, 점프 억제
- 점프만 착지까지 재생한 뒤 전환. 드래그 시작 시 점프 즉시 중단
- 달리기와 그 외 동작은 행동 종료 시 즉시 전환
- 위쪽은 점프 최고점이 작업 영역 경계에 닿도록 위치 제한
- 좌우 고정 여백 제거: 가장 넓은 동작의 실제 픽셀을 기준으로 제한
- 아래쪽은 발끝 기준으로 작업 표시줄에 붙일 수 있음
- 100% 크기: Windows 배율과 관계없이 실제 192×208픽셀
- 작업 표시줄/Alt+Tab 창 표시 없음, 트레이 메뉴 제공
- 메뉴는 트레이에서만 열림. 미쿠 자체의 우클릭 메뉴 없음
- 일시정지·숨김·크기·대기 시간·항상 위에 표시·위치 저장

Windows 기본 .NET Framework 4.x와 Win32 API를 사용합니다.
애니메이션은 캐시한 비트맵을 사용하고 입력은 Raw Input으로 감지합니다.
숨김·일시정지 시 입력 등록과 애니메이션 타이머를 중지합니다.
입력 내용 저장이나 네트워크 통신은 없습니다.

앱 아이콘의 알림 영역 위치는 Windows가 관리합니다.
멀티 모니터/DPI 경계는 구현 및 좌표 검증을 했으며,
이 PC의 실제 네이티브 창 검증 결과는 `검증결과.txt`에 기록했습니다.

`source/build.ps1`로 다시 빌드할 수 있습니다.
실행 파일에 스프라이트가 포함되어 있어 실행 시 source 폴더는 필요하지 않습니다.

기술 근거: [Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input),
[레이어 창](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features),
[트레이 아이콘](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area).
