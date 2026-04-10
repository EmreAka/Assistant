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

- [ ] Full-text search için TR / EN karma kullanım testlerini genişlet.
Gerekirse `simple` config yerine farklı bir yaklaşım değerlendir.

- [ ] Semantic retrieval ihtiyacını değerlendir.
Full-text yeterli değilse embedding tabanlı ikinci aşama retrieval eklenebilir.
