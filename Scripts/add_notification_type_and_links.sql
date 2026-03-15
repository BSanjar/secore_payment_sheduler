-- Добавление типа уведомления и привязок к счёту/платежу/организации.
-- Выполнить на БД перед деплоем, если колонок ещё нет.
--
-- Для сервиса-отправителя: перед отправкой проверять актуальность (например, оплачен ли платёж).
-- Если обязательство уже закрыто — не отправлять уведомление, а выставить status = 'cancelled'.

ALTER TABLE notifications
  ADD COLUMN IF NOT EXISTS notification_type character varying,
  ADD COLUMN IF NOT EXISTS invoice_id character varying,
  ADD COLUMN IF NOT EXISTS invoice_payment_id character varying,
  ADD COLUMN IF NOT EXISTS organization_id character varying;

-- Уникальный индекс: один тип уведомления на платёж и канал (защита от дублей).
CREATE UNIQUE INDEX IF NOT EXISTS ix_notifications_type_payment_channel_uq
  ON notifications (notification_type, invoice_payment_id, channel)
  WHERE invoice_payment_id IS NOT NULL;

-- Уникальный индекс: один тип уведомления на организацию и канал (подписки).
CREATE UNIQUE INDEX IF NOT EXISTS ix_notifications_type_org_channel_uq
  ON notifications (notification_type, organization_id, channel)
  WHERE organization_id IS NOT NULL;
