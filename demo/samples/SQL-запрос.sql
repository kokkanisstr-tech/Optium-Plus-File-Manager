CREATE TABLE Students (
    Id INTEGER PRIMARY KEY,
    FullName TEXT NOT NULL,
    GroupName TEXT NOT NULL
);

INSERT INTO Students (Id, FullName, GroupName)
VALUES (1, 'Пример Студент', 'ИС-21');

SELECT FullName, GroupName
FROM Students
WHERE GroupName = 'ИС-21';
