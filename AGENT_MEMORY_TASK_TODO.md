# Agent Memory & Task TODO

Bu dosya agent tarafında yaptığımız `task management` ve `memory` iyileştirmeleri için çalışma notudur.

## Tamamlananlar

- [x] `ScheduleTask` yanına `ListTasks`, `CancelTask`, `RescheduleTask` araçları eklendi.
- [x] Task yönetimi için Hangfire işlemlerini izole eden `IDeferredIntentScheduler` / `DeferredIntentScheduler` eklendi.
- [x] Pending task context recurring görevleri de gösterecek şekilde güncellendi.
- [x] Pending task context içine `Task ID` bilgisi eklendi.
- [x] Agent prompt'u task listeleme / iptal / yeniden zamanlama kurallarını içerecek şekilde güncellendi.
- [x] Task tool testleri eklendi ve geçti.

- [x] Memory tarafına `ListMemories`, `UpdateMemory`, `DeleteMemory` araçları eklendi.
- [x] Agent prompt'u memory listeleme / güncelleme / silme kurallarını içerecek şekilde güncellendi.
- [x] Memory context içine `Memory ID` bilgisi eklendi.
- [x] Memory retrieval query-aware hale getirildi.
- [x] Memory arama için PostgreSQL full-text search altyapısı eklendi.
- [x] `user_memories.search_vector` + GIN index için EF Core migration üretildi.
- [x] `AddUserMemorySearchVector` migration'ı veritabanına uygulandı.
- [x] Memory tool ve memory service testleri eklendi ve geçti.

## Kalanlar

- [ ] `TouchMemoriesAsync` mantığını düzelt.
Şu an context'e seçilen memory'ler otomatik olarak `used` sayılıyor. İdeal olan gerçekten cevapta kullanılan memory'yi işaretlemek.

- [ ] Memory conflict resolution ekle.
Yeni memory, eski bir memory ile çelişiyorsa duplicate açmak yerine eski kaydı güncelle veya supersede et.

- [ ] Memory metadata modelini genişlet.
Olası alanlar: `Kind`, `Confidence`, `LastConfirmedAt`, `SourceTurnId`, `IsActive`, `SupersededByMemoryId`.

- [ ] Memory tool çıktılarını kullanıcı tarafında daha doğal hale getir.
`ListMemories`, `UpdateMemory`, `DeleteMemory` cevapları daha kısa ve daha doğal özet dönebilir.

- [ ] In-memory agent session yapısını kalıcı hale getir.
Restart veya scale-out sonrası conversation continuity kırılmasın.

- [ ] Task geçmişi görünümü ekle.
Sadece aktif görevler değil, completed / failed geçmişi de kullanıcı dostu özetlenebilsin.

- [ ] Task iptal / güncelleme tarafında belirsizlik çözümü ekle.
Benzer isimli birden fazla görev olduğunda kısa bir doğrulama stratejisi gerekebilir.

- [ ] Full-text search için TR / EN karma kullanım testlerini genişlet.
Gerekirse `simple` config yerine farklı bir yaklaşım değerlendir.

- [ ] Semantic retrieval ihtiyacını değerlendir.
Full-text yeterli değilse embedding tabanlı ikinci aşama retrieval eklenebilir.

- [ ] Golden conversation testleri yaz.
Memory save / update / delete / retrieval ve task change senaryoları uçtan uca doğrulansın.

- [ ] Tool telemetry ve observability geliştir.
Hangi tool ne zaman çağrıldı, başarı / hata oranı ne, daha görünür hale getir.
